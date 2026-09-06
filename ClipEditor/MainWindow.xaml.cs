using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using LibVLCSharp.Shared;
using FFMpegCore;

namespace ClipEditor
{
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        private const string MontageAudioBitrateArg = "128k";
        private const double MontageAudioBitsPerSecond = 128_000;

        // Bounds for the auto-computed montage video bitrate. The export
        // scales down toward the floor to fit the selected size cap; if even
        // the floor won't fit, we tell the user to trim more.
        private const double MinVideoBitsPerSecond = 2_000_000;
        private const double MaxVideoBitsPerSecond = 12_000_000;

        private static readonly string[] VideoExtensions =
            { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v" };

        private readonly AppSettings _settings;
        private string _ffmpegBinFolder;
        private bool _ffmpegReady;

        // Replaced with "h264_nvenc" once the startup probe confirms the
        // machine has an NVIDIA encoder; otherwise we stay on the CPU codec.
        private string _videoCodec = "libx264";

        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private string _currentFilePath;

        private DispatcherTimer _positionTimer;

        // LibVLC won't resume a media that has played all the way to the end
        // -- it has to be reloaded first. We remember that state here and
        // defer whatever seek the user asks for until the reloaded media is
        // actually ready to accept it.
        private bool _isEnded;
        private long _pendingSeekMs = -1;
        private bool _pauseAfterPendingSeek;

        // Only reset the in/out selection when a brand-new file is opened,
        // not when the current media is reloaded to recover from the end.
        private bool _resetTrimOnNextLength;

        // While previewing just the in/out selection, playback pauses
        // once it passes this point. -1 means "play through as normal".
        private double _selectionStopSeconds = -1;

        private CancellationTokenSource _exportCts;

        // Filmstrip generation for the timeline. Each new video cancels the
        // scan still running for the previous one.
        private const int ThumbnailCount = 24;
        private CancellationTokenSource _thumbnailCts;

        // One queued trim: the temp file, the source it came from, and how
        // long it runs. Order in the list is montage order.
        private sealed record MontageClip(string Path, string SourceName, double Duration);

        private readonly List<MontageClip> _clips = new List<MontageClip>();

        private enum SizePreset { Discord50, Discord20, Full }

        public MainWindow()
        {
            InitializeComponent();

            _settings = AppSettings.Load();
            RestoreWindowPlacement();

            Core.Initialize();

            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);

            VideoViewControl.MediaPlayer = _mediaPlayer;

            // LibVLC raises these on its own worker threads. Always marshal
            // back with BeginInvoke (async) -- a synchronous Invoke here can
            // deadlock against a Stop()/Play() call on the UI thread.
            _mediaPlayer.LengthChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    double lengthSeconds = e.Length / 1000.0;
                    TrimTimeline.Duration = lengthSeconds;

                    if (_resetTrimOnNextLength)
                    {
                        _resetTrimOnNextLength = false;
                        TrimTimeline.InPoint = 0;
                        TrimTimeline.OutPoint = lengthSeconds;
                        UpdateTrimLabels();
                        _ = GenerateThumbnailsAsync(_currentFilePath, lengthSeconds);
                    }
                });
            };

            _mediaPlayer.EndReached += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _isEnded = true;
                    PlayPauseButton.Content = "Play";
                });
            };

            // When a reloaded media starts playing, apply whatever seek was
            // queued while it was loading.
            _mediaPlayer.Playing += (s, e) => Dispatcher.BeginInvoke(() =>
            {
                _isEnded = false;
                ApplyPendingSeek();
            });

            _positionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            PresetComboBox.SelectedIndex = Math.Clamp(_settings.PresetIndex, 0, 2);
            UpdateOutputFolderLabel();
            UpdateTrimLabels();
            RefreshQueueList();

            Loaded += async (s, e) => await InitializeFfmpegAsync();
        }

        // ----- Window placement --------------------------------------------

        private void RestoreWindowPlacement()
        {
            if (_settings.WindowWidth > 200 && _settings.WindowHeight > 200)
            {
                Width = _settings.WindowWidth;
                Height = _settings.WindowHeight;
            }

            // Only restore the position if it lands on a screen that still
            // exists, otherwise the window could open off in the void.
            bool onScreen =
                _settings.WindowLeft >= SystemParameters.VirtualScreenLeft &&
                _settings.WindowTop >= SystemParameters.VirtualScreenTop &&
                _settings.WindowLeft + 100 <=
                    SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                _settings.WindowTop + 100 <=
                    SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

            if (onScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
            }

            if (_settings.WindowMaximized)
                WindowState = WindowState.Maximized;
        }

        private void SaveWindowPlacement()
        {
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            _settings.Save();
        }

        // ----- Paths / settings -------------------------------------------------

        private string DefaultExportFolder =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "EditedClips");

        // The configured default the save dialogs start from.
        private string ExportFolder
        {
            get
            {
                string folder = string.IsNullOrWhiteSpace(_settings.ExportFolder)
                    ? DefaultExportFolder
                    : _settings.ExportFolder;

                try { Directory.CreateDirectory(folder); } catch { }
                return folder;
            }
        }

        // Where the last export was saved -- used as the dialog's starting
        // point so it reopens where you left off, without overriding the
        // configured default.
        private string LastUsedFolder
        {
            get
            {
                string last = _settings.LastExportFolder;
                return !string.IsNullOrWhiteSpace(last) && Directory.Exists(last)
                    ? last
                    : ExportFolder;
            }
            set
            {
                _settings.LastExportFolder = value;
                _settings.Save();
            }
        }

        private string LastVideoFolder =>
            !string.IsNullOrWhiteSpace(_settings.LastVideoFolder) &&
            Directory.Exists(_settings.LastVideoFolder)
                ? _settings.LastVideoFolder
                : ExportFolder;

        private void UpdateOutputFolderLabel()
        {
            OutputFolderLabel.Text = ExportFolder;
            OutputFolderLabel.ToolTip = ExportFolder;
        }

        private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose where exports are saved by default",
                InitialDirectory = ExportFolder
            };

            if (dialog.ShowDialog() == true)
            {
                _settings.ExportFolder = dialog.FolderName;
                _settings.Save();
                UpdateOutputFolderLabel();
            }
        }

        private void ResetOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            _settings.ExportFolder = null;
            _settings.Save();
            UpdateOutputFolderLabel();
        }

        // ----- FFmpeg discovery ----------------------------------------------

        private async Task InitializeFfmpegAsync()
        {
            _ffmpegBinFolder = ResolveFfmpegFolder(_settings.FfmpegBinFolder);

            if (_ffmpegBinFolder != null)
            {
                GlobalFFOptions.Configure(new FFOptions { BinaryFolder = _ffmpegBinFolder });
                _ffmpegReady = true;
            }
            else
            {
                _ffmpegReady = false;
                MessageBox.Show(
                    "Couldn't find ffmpeg.exe.\n\n" +
                    "Install FFmpeg and either add its bin folder to your PATH, or set " +
                    "\"FfmpegBinFolder\" in:\n\n" + AppSettings.SettingsPath,
                    "FFmpeg not found");
            }

            UpdateExportButtons();

            await DetectVideoEncoderAsync();
        }

        // Looks in the configured folder, then a few common install spots,
        // then every folder on PATH. Returns null if nothing turns up.
        private static string ResolveFfmpegFolder(string configured)
        {
            bool HasFfmpeg(string dir) =>
                !string.IsNullOrWhiteSpace(dir) &&
                File.Exists(Path.Combine(dir, "ffmpeg.exe"));

            if (HasFfmpeg(configured))
                return configured;

            string[] candidates =
            {
                @"C:\ffmpeg-9.0.1-essentials_build\bin",
                @"C:\ffmpeg\bin",
                @"C:\Program Files\ffmpeg\bin",
            };
            foreach (var candidate in candidates)
                if (HasFfmpeg(candidate))
                    return candidate;

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    string trimmed = dir.Trim().Trim('"');
                    if (HasFfmpeg(trimmed))
                        return trimmed;
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }

            return null;
        }

        // h264_nvenc only exists on NVIDIA cards. Probe once at startup and
        // fall back to libx264 everywhere else so the export still works.
        private async Task DetectVideoEncoderAsync()
        {
            try
            {
                string exe = _ffmpegBinFolder != null
                    ? Path.Combine(_ffmpegBinFolder, "ffmpeg.exe")
                    : "ffmpeg";

                var psi = new ProcessStartInfo(exe, "-hide_banner -encoders")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = Process.Start(psi);
                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (output.Contains("h264_nvenc"))
                    _videoCodec = "h264_nvenc";
            }
            catch
            {
                // Keep the libx264 default.
            }
        }

        // ----- Playback ------------------------------------------------------

        private void PositionTimer_Tick(object sender, EventArgs e)
        {
            ApplyPendingSeek();

            if (_mediaPlayer.Length <= 0)
                return;

            // Previewing a selection: stop when we reach the out point. Skip
            // while a seek is still pending, since Time is briefly stale.
            if (_selectionStopSeconds >= 0 && _pendingSeekMs < 0 &&
                _mediaPlayer.Time / 1000.0 >= _selectionStopSeconds)
            {
                _selectionStopSeconds = -1;
                _mediaPlayer.SetPause(true);
                PlayPauseButton.Content = "Play";
            }

            if (!TrimTimeline.IsUserDragging)
                TrimTimeline.Position = _mediaPlayer.Time / 1000.0;

            var current = TimeSpan.FromMilliseconds(_mediaPlayer.Time);
            var total = TimeSpan.FromMilliseconds(_mediaPlayer.Length);
            TimeLabel.Text = $"{current:mm\\:ss} / {total:mm\\:ss}";
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath))
                return;

            _selectionStopSeconds = -1;

            if (IsFinished)
            {
                // Can't resume a finished media -- reload it and play from
                // the start.
                ReloadCurrentMedia();
                PlayPauseButton.Content = "Pause";
                return;
            }

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                PlayPauseButton.Content = "Play";
            }
            else
            {
                _mediaPlayer.Play();
                PlayPauseButton.Content = "Pause";
            }
        }

        // Plays only the trimmed range so you can preview the clip you are
        // about to queue, instead of scrubbing through the whole video.
        private void PlaySelection_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath) || _mediaPlayer.Length <= 0)
                return;

            double inPoint = TrimTimeline.InPoint;
            double outPoint = TrimTimeline.OutPoint;

            if (outPoint <= inPoint)
            {
                MessageBox.Show("Set an in point and an out point on the timeline first.");
                return;
            }

            SeekTo(inPoint, resumePlayback: true); // also clears any previous preview
            TrimTimeline.Position = inPoint;

            if (!IsFinished && !_mediaPlayer.IsPlaying)
                _mediaPlayer.Play();

            _selectionStopSeconds = outPoint;
            PlayPauseButton.Content = "Pause";
        }

        private void FrameBack_Click(object sender, RoutedEventArgs e) => StepFrame(-1);

        private void FrameForward_Click(object sender, RoutedEventArgs e) => StepFrame(+1);

        private double FrameStepSeconds()
        {
            double fps = _mediaPlayer.Fps;
            if (fps <= 0 || double.IsNaN(fps))
                fps = 30;
            return 1.0 / fps;
        }

        private double CurrentPositionSeconds()
        {
            if (_pendingSeekMs >= 0)
                return _pendingSeekMs / 1000.0;
            return _mediaPlayer.Length > 0 ? _mediaPlayer.Time / 1000.0 : 0;
        }

        private void StepFrame(int direction)
        {
            if (_mediaPlayer.Length <= 0)
                return;

            _mediaPlayer.SetPause(true);
            PlayPauseButton.Content = "Play";

            double max = _mediaPlayer.Length / 1000.0;
            double target = Math.Clamp(
                CurrentPositionSeconds() + direction * FrameStepSeconds(), 0, max);

            SeekTo(target, resumePlayback: false);
            TrimTimeline.Position = target;
        }

        private void NudgeSeconds(double delta)
        {
            if (_mediaPlayer.Length <= 0)
                return;

            double max = _mediaPlayer.Length / 1000.0;
            double target = Math.Clamp(CurrentPositionSeconds() + delta, 0, max);

            SeekTo(target, resumePlayback: true);
            TrimTimeline.Position = target;
        }

        // Applies a seek that was deferred while a reloaded media was still
        // loading (LibVLC can't seek a media that has reached its end, so we
        // reload it and wait for it to be ready).
        private void ApplyPendingSeek()
        {
            if (_pendingSeekMs < 0)
                return;

            if (_mediaPlayer.Length <= 0 || !_mediaPlayer.IsSeekable)
                return;

            _mediaPlayer.Time = _pendingSeekMs;

            if (_pauseAfterPendingSeek)
            {
                _mediaPlayer.SetPause(true);
                PlayPauseButton.Content = "Play";
            }
            else
            {
                PlayPauseButton.Content = "Pause";
            }

            _pendingSeekMs = -1;
            _pauseAfterPendingSeek = false;
        }

        private bool IsFinished =>
            _isEnded || _mediaPlayer.State is VLCState.Ended or VLCState.Stopped;

        // Seeks to a point in the current video. If the media already played
        // to the end, it's reloaded first and the seek is queued until the
        // fresh media is ready to accept it.
        private void SeekTo(double seconds, bool resumePlayback)
        {
            _selectionStopSeconds = -1;

            long ms = (long)Math.Max(0, seconds * 1000);

            if (!IsFinished)
            {
                _mediaPlayer.Time = ms;
                return;
            }

            if (_pendingSeekMs < 0)
                ReloadCurrentMedia();

            _pendingSeekMs = ms;
            _pauseAfterPendingSeek = !resumePlayback;
        }

        private void ReloadCurrentMedia()
        {
            if (string.IsNullOrEmpty(_currentFilePath))
                return;

            _isEnded = false;

            // A media that has ended has to be fully stopped before it will
            // play again. Stop() blocks until LibVLC's threads wind down, so
            // run the whole restart off the UI thread -- the player's methods
            // are safe to call from any thread.
            string path = _currentFilePath;
            Task.Run(() =>
            {
                try
                {
                    _mediaPlayer.Stop();
                    using var media = new Media(_libVLC, path, FromType.FromPath);
                    _mediaPlayer.Play(media);
                }
                catch
                {
                    // The window may be closing; nothing useful to do here.
                }
            });
        }

        private void LoadVideo(string path)
        {
            if (!File.Exists(path))
                return;

            _currentFilePath = path;
            _settings.LastVideoFolder = Path.GetDirectoryName(path);
            _settings.Save();

            _isEnded = false;
            _pendingSeekMs = -1;
            _pauseAfterPendingSeek = false;
            _resetTrimOnNextLength = true;
            _selectionStopSeconds = -1;

            TrimTimeline.Position = 0;
            TrimTimeline.InPoint = 0;
            TrimTimeline.OutPoint = 0;
            UpdateTrimLabels();

            using var media = new Media(_libVLC, path, FromType.FromPath);
            _mediaPlayer.Play(media);
            PlayPauseButton.Content = "Pause";
        }

        private void OpenVideo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Video files (*.mp4;*.mkv;*.mov)|*.mp4;*.mkv;*.mov|All files (*.*)|*.*",
                InitialDirectory = LastVideoFolder
            };

            if (dialog.ShowDialog() == true)
                LoadVideo(dialog.FileName);
        }

        // ----- Timeline events --------------------------------------------

        private void TrimTimeline_SeekRequested(object sender, double seconds)
        {
            // Scrubbing the playhead keeps playback going; after the video
            // has ended this is what lets you pick up again from an earlier
            // point instead of being stuck on the last frame.
            SeekTo(seconds, resumePlayback: true);
        }

        private void TrimTimeline_ScrubPreview(object sender, double seconds)
        {
            _mediaPlayer.SetPause(true);
            PlayPauseButton.Content = "Play";
            SeekTo(seconds, resumePlayback: false);
        }

        private void TrimTimeline_InOutChanged(object sender, EventArgs e) => UpdateTrimLabels();

        private void SetIn_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.Length <= 0)
                return;

            double pos = CurrentPositionSeconds();
            if (TrimTimeline.OutPoint > 0 && pos >= TrimTimeline.OutPoint)
            {
                MessageBox.Show("The in point has to come before the out point.");
                return;
            }

            TrimTimeline.InPoint = pos;
            UpdateTrimLabels();
        }

        private void SetOut_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.Length <= 0)
                return;

            double pos = CurrentPositionSeconds();
            if (pos <= TrimTimeline.InPoint)
            {
                MessageBox.Show("Scrub past the in point before setting the out point.");
                return;
            }

            TrimTimeline.OutPoint = pos;
            UpdateTrimLabels();
        }

        private void UpdateTrimLabels()
        {
            StartLabel.Text = $"In {FormatTime(TrimTimeline.InPoint)}";
            EndLabel.Text = $"Out {FormatTime(TrimTimeline.OutPoint)}";

            double selection = Math.Max(0, TrimTimeline.OutPoint - TrimTimeline.InPoint);
            SelectionLabel.Text = selection > 0 ? $"selection {selection:0.0}s" : "";
        }

        private static string FormatTime(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds))
                seconds = 0;
            return TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.ff");
        }

        // ----- Keyboard ----------------------------------------------------

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Let the caption box (or any text field) keep normal typing.
            if (Keyboard.FocusedElement is TextBox)
                return;

            switch (e.Key)
            {
                case Key.Space:
                    PlayPause_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.P:
                    PlaySelection_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.OemOpenBrackets:   // [
                    SetIn_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.OemCloseBrackets:  // ]
                    SetOut_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.OemComma:          // ,
                    StepFrame(-1);
                    e.Handled = true;
                    break;
                case Key.OemPeriod:         // .
                    StepFrame(+1);
                    e.Handled = true;
                    break;
                case Key.Left:
                    NudgeSeconds(-5);
                    e.Handled = true;
                    break;
                case Key.Right:
                    NudgeSeconds(+5);
                    e.Handled = true;
                    break;
            }
        }

        // ----- Drag and drop ---------------------------------------------

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = TryGetDroppedVideo(e, out _)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (TryGetDroppedVideo(e, out string path))
                LoadVideo(path);
        }

        private static bool TryGetDroppedVideo(DragEventArgs e, out string path)
        {
            path = null;

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return false;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            path = files?.FirstOrDefault(f =>
                VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

            return path != null;
        }

        // ----- Montage queue ---------------------------------------------

        // Trims the current selection into a temporary file and adds it to
        // the clip queue. Uses CopyChannel (fast, no re-encode) since the
        // whole queue gets re-encoded together anyway on export.
        private async void AddToMontage_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                MessageBox.Show("Open a video first.");
                return;
            }

            double inPoint = TrimTimeline.InPoint;
            double outPoint = TrimTimeline.OutPoint;

            if (outPoint <= inPoint)
            {
                MessageBox.Show("Set an in point and an out point on the timeline first.");
                return;
            }

            if (!EnsureFfmpegReady())
                return;

            string tempClipPath = Path.Combine(Path.GetTempPath(), $"montage_clip_{Guid.NewGuid()}.mp4");

            var startTime = TimeSpan.FromSeconds(inPoint);
            var duration = TimeSpan.FromSeconds(outPoint - inPoint);

            try
            {
                await FFMpegArguments
                    .FromFileInput(_currentFilePath, verifyExists: true,
                        options => options.Seek(startTime))
                    .OutputToFile(tempClipPath, overwrite: true,
                        options => options.WithDuration(duration).CopyChannel())
                    .ProcessAsynchronously();

                _clips.Add(new MontageClip(
                    tempClipPath, Path.GetFileName(_currentFilePath), outPoint - inPoint));

                RefreshQueueList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't add clip: " + ex.Message);
            }
        }

        private void RemoveFromMontage_Click(object sender, RoutedEventArgs e)
        {
            int index = MontageListBox.SelectedIndex;
            if (index < 0)
            {
                MessageBox.Show("Select a clip in the list first.");
                return;
            }

            TryDeleteFile(_clips[index].Path);
            _clips.RemoveAt(index);
            RefreshQueueList();
        }

        private void ClearMontage_Click(object sender, RoutedEventArgs e)
        {
            foreach (var clip in _clips)
                TryDeleteFile(clip.Path);

            _clips.Clear();
            RefreshQueueList();
        }

        private void MoveClipUp_Click(object sender, RoutedEventArgs e) => MoveSelectedClip(-1);

        private void MoveClipDown_Click(object sender, RoutedEventArgs e) => MoveSelectedClip(+1);

        private void MoveSelectedClip(int direction)
        {
            int index = MontageListBox.SelectedIndex;
            int target = index + direction;

            if (index < 0 || target < 0 || target >= _clips.Count)
                return;

            (_clips[index], _clips[target]) = (_clips[target], _clips[index]);
            RefreshQueueList();
            MontageListBox.SelectedIndex = target;
        }

        // Rebuilds every row so the numbering, per-clip length and running
        // total stay correct after any add / remove / reorder.
        private void RefreshQueueList()
        {
            int selected = MontageListBox.SelectedIndex;

            MontageListBox.Items.Clear();

            double cumulative = 0;
            for (int i = 0; i < _clips.Count; i++)
            {
                cumulative += _clips[i].Duration;
                MontageListBox.Items.Add(
                    $"Clip {i + 1} — {_clips[i].SourceName}  ·  " +
                    $"{_clips[i].Duration:0.0}s   (Σ {cumulative:0.0}s)");
            }

            if (_clips.Count > 0)
                MontageListBox.SelectedIndex = Math.Clamp(selected, 0, _clips.Count - 1);

            UpdateSizeEstimate();
        }

        private string WriteConcatListFile()
        {
            string listFilePath = Path.Combine(Path.GetTempPath(), $"montage_list_{Guid.NewGuid()}.txt");
            File.WriteAllLines(listFilePath, _clips.Select(c => $"file '{c.Path}'"));
            return listFilePath;
        }

        // ----- Size estimate / bitrate -----------------------------------

        private SizePreset CurrentPreset =>
            (SizePreset)Math.Clamp(PresetComboBox.SelectedIndex, 0, 2);

        private double? SizeCapMB => CurrentPreset switch
        {
            SizePreset.Discord50 => 50.0,
            SizePreset.Discord20 => 20.0,
            _ => (double?)null,
        };

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _settings.PresetIndex = PresetComboBox.SelectedIndex;
            _settings.Save();
            UpdateSizeEstimate();
        }

        // Picks the video bitrate the export will use: the full-quality
        // target, scaled down toward the floor only as far as needed to fit
        // the selected size cap.
        private double ComputeVideoBitsPerSecond(double totalSeconds, double capMB)
        {
            if (totalSeconds <= 0)
                return MaxVideoBitsPerSecond;

            const double headroom = 0.95; // container overhead + bitrate wobble
            double totalBudgetBits = capMB * 1024 * 1024 * 8 * headroom;
            double videoBudgetBits = totalBudgetBits - MontageAudioBitsPerSecond * totalSeconds;
            double bps = videoBudgetBits / totalSeconds;

            return Math.Clamp(bps, MinVideoBitsPerSecond, MaxVideoBitsPerSecond);
        }

        private void UpdateSizeEstimate()
        {
            if (_clips.Count == 0)
            {
                SizeEstimateLabel.Text = "Estimated size: --";
                SizeEstimateLabel.ClearValue(TextBlock.ForegroundProperty);
                return;
            }

            double totalSeconds = _clips.Sum(c => c.Duration);

            if (SizeCapMB is not double cap)
            {
                SizeEstimateLabel.Text = $"Full quality · {totalSeconds:0.0}s total (no size cap)";
                SizeEstimateLabel.ClearValue(TextBlock.ForegroundProperty);
                return;
            }

            double videoBps = ComputeVideoBitsPerSecond(totalSeconds, cap);
            double estimatedMB =
                totalSeconds * (videoBps + MontageAudioBitsPerSecond) / 8.0 / (1024.0 * 1024.0);

            bool wontFit = videoBps <= MinVideoBitsPerSecond && estimatedMB > cap;

            if (wontFit)
            {
                SizeEstimateLabel.Text =
                    $"~{estimatedMB:0.0} MB — too long for {cap:0} MB even at low quality; trim more";
                SizeEstimateLabel.Foreground = System.Windows.Media.Brushes.IndianRed;
            }
            else
            {
                SizeEstimateLabel.Text = $"~{estimatedMB:0.0} MB  /  {cap:0} MB cap";
                SizeEstimateLabel.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;
            }
        }

        // ----- Export ----------------------------------------------------

        // The one and only video export: joins everything in the queue
        // (even if it's just one clip) into a single file, optionally
        // cropping it vertical and/or burning in a caption.
        private async void ExportMontage_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureFfmpegReady())
                return;

            if (_clips.Count == 0)
            {
                MessageBox.Show("Add at least one clip first (set in/out, then \"Add Clip\").");
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "MP4 video (*.mp4)|*.mp4",
                FileName = "export.mp4",
                InitialDirectory = LastUsedFolder
            };

            if (saveDialog.ShowDialog() != true)
                return;

            LastUsedFolder = Path.GetDirectoryName(saveDialog.FileName);

            string listFilePath = WriteConcatListFile();
            double totalSeconds = _clips.Sum(c => c.Duration);
            var totalDuration = TimeSpan.FromSeconds(totalSeconds);

            _exportCts = new CancellationTokenSource();
            SetExportInProgress(true, "Exporting video…");

            try
            {
                await FFMpegArguments
                    .FromFileInput(listFilePath, verifyExists: true,
                        options => options
                            .WithCustomArgument("-f concat")
                            .WithCustomArgument("-safe 0"))
                    .OutputToFile(saveDialog.FileName, overwrite: true,
                        options => BuildVideoOutput(options, totalSeconds))
                    .CancellableThrough(_exportCts.Token)
                    .NotifyOnProgress(
                        percent => Dispatcher.Invoke(() => ReportExportProgress(percent)),
                        totalDuration)
                    .ProcessAsynchronously();

                MessageBox.Show("Exported to " + saveDialog.FileName);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(saveDialog.FileName);
                MessageBox.Show("Export canceled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export failed: " + ex.Message +
                    "\n\nThis works best when all your queued clips share the same " +
                    "resolution and frame rate, which is normally true for your own " +
                    "gameplay captures.");
            }
            finally
            {
                SetExportInProgress(false);
                _exportCts?.Dispose();
                _exportCts = null;
                TryDeleteFile(listFilePath);
            }
        }

        private void BuildVideoOutput(FFMpegArgumentOptions options, double totalSeconds)
        {
            options
                .WithVideoCodec(_videoCodec)
                .WithAudioCodec("aac")
                .WithCustomArgument("-pix_fmt yuv420p");

            if (SizeCapMB is double cap)
            {
                double videoBps = ComputeVideoBitsPerSecond(totalSeconds, cap);
                long kbit = (long)Math.Round(videoBps / 1000.0);

                options
                    .WithCustomArgument($"-b:v {kbit}k")
                    .WithCustomArgument($"-maxrate {kbit}k")
                    .WithCustomArgument($"-bufsize {kbit * 2}k")
                    .WithCustomArgument($"-b:a {MontageAudioBitrateArg}");

                if (_videoCodec == "libx264")
                    options.WithCustomArgument("-preset veryfast");
            }
            else
            {
                // Full quality -- constant-quality encode, no size target.
                options.WithCustomArgument("-b:a 192k");

                if (_videoCodec == "libx264")
                    options
                        .WithCustomArgument("-crf 20")
                        .WithCustomArgument("-preset veryfast");
                else
                    options
                        .WithCustomArgument("-rc constqp")
                        .WithCustomArgument("-qp 20");
            }

            // FFmpeg only allows one -vf per export, so we build up a single
            // comma-separated chain of whichever effects are turned on.
            var filters = new List<string>();

            if (VerticalCropCheckBox.IsChecked == true)
            {
                filters.Add("crop=ih*9/16:ih");
                filters.Add("scale=1080:1920:flags=lanczos");
            }

            if (!string.IsNullOrWhiteSpace(CaptionTextBox.Text))
            {
                bool isVertical = VerticalCropCheckBox.IsChecked == true;

                string safeCaption = EscapeForDrawText(CaptionTextBox.Text);
                var captionLines = SplitCaptionIntoLines(safeCaption, isVertical);

                // y=h/8 places the first line 7/8 of the way up from the
                // bottom; +h/20 nudges it down a little further. Each extra
                // line adds its own drawtext filter, offset lower by roughly
                // one line's height (fontsize * 1.25).
                const int fontSize = 72;
                const int lineHeightPx = 90; // ~ fontSize * 1.25

                for (int i = 0; i < captionLines.Count; i++)
                {
                    string yExpr = i == 0
                        ? "h/8+h/20"
                        : $"h/8+h/20+{i * lineHeightPx}";

                    filters.Add(
                        $"drawtext=fontfile='C\\:/Windows/Fonts/comicbd.ttf':text='{captionLines[i]}':" +
                        $"fontsize={fontSize}:fontcolor=white:borderw=2:bordercolor=black:" +
                        $"x=(w-text_w)/2:y={yExpr}");
                }
            }

            if (filters.Count > 0)
                options.WithCustomArgument($"-vf \"{string.Join(",", filters)}\"");
        }

        // Same queue, but strips all video and saves only the sound.
        private async void ExportMontageAudio_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureFfmpegReady())
                return;

            if (_clips.Count == 0)
            {
                MessageBox.Show("Add at least one clip first (set in/out, then \"Add Clip\").");
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "MP3 audio (*.mp3)|*.mp3",
                FileName = "audio.mp3",
                InitialDirectory = LastUsedFolder
            };

            if (saveDialog.ShowDialog() != true)
                return;

            LastUsedFolder = Path.GetDirectoryName(saveDialog.FileName);

            string listFilePath = WriteConcatListFile();
            var totalDuration = TimeSpan.FromSeconds(_clips.Sum(c => c.Duration));

            _exportCts = new CancellationTokenSource();
            SetExportInProgress(true, "Exporting audio…");

            try
            {
                await FFMpegArguments
                    .FromFileInput(listFilePath, verifyExists: true,
                        options => options
                            .WithCustomArgument("-f concat")
                            .WithCustomArgument("-safe 0"))
                    .OutputToFile(saveDialog.FileName, overwrite: true,
                        options => options
                            .WithCustomArgument("-vn")
                            .WithAudioCodec("libmp3lame"))
                    .CancellableThrough(_exportCts.Token)
                    .NotifyOnProgress(
                        percent => Dispatcher.Invoke(() => ReportExportProgress(percent)),
                        totalDuration)
                    .ProcessAsynchronously();

                MessageBox.Show("Audio exported to " + saveDialog.FileName);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(saveDialog.FileName);
                MessageBox.Show("Export canceled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Audio export failed: " + ex.Message);
            }
            finally
            {
                SetExportInProgress(false);
                _exportCts?.Dispose();
                _exportCts = null;
                TryDeleteFile(listFilePath);
            }
        }

        private void CancelExport_Click(object sender, RoutedEventArgs e)
        {
            _exportCts?.Cancel();
            ExportStatusLabel.Text = "Canceling…";
            CancelExportButton.IsEnabled = false;
        }

        private bool EnsureFfmpegReady()
        {
            if (_ffmpegReady)
                return true;

            MessageBox.Show(
                "FFmpeg isn't set up yet.\n\n" +
                "Add its bin folder to your PATH, or set \"FfmpegBinFolder\" in:\n\n" +
                AppSettings.SettingsPath,
                "FFmpeg not found");
            return false;
        }

        private void UpdateExportButtons()
        {
            ExportVideoButton.IsEnabled = _ffmpegReady;
            ExportAudioButton.IsEnabled = _ffmpegReady;
        }

        private void SetExportInProgress(bool busy, string status = null)
        {
            ExportVideoButton.IsEnabled = !busy && _ffmpegReady;
            ExportAudioButton.IsEnabled = !busy && _ffmpegReady;

            ExportProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ExportProgressBar.Value = 0;
            ExportProgressBar.IsIndeterminate = busy; // until the first progress tick
            ExportStatusLabel.Text = busy ? (status ?? "Working…") : "";
            CancelExportButton.IsEnabled = busy;
        }

        private void ReportExportProgress(double percent)
        {
            ExportProgressBar.IsIndeterminate = false;
            ExportProgressBar.Value = Math.Clamp(percent, 0, 100);
            ExportStatusLabel.Text = $"{percent:0}%";
        }

        // ----- Timeline filmstrip -------------------------------------------

        // Pulls evenly spaced frames out of the video with one ffmpeg pass and
        // hands them to the timeline to draw behind the track.
        private async Task GenerateThumbnailsAsync(string videoPath, double durationSeconds)
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts?.Dispose();

            var cts = new CancellationTokenSource();
            _thumbnailCts = cts;

            TrimTimeline.SetThumbnails(Array.Empty<System.Windows.Media.ImageSource>());

            if (!_ffmpegReady || durationSeconds <= 0 || string.IsNullOrEmpty(videoPath))
                return;

            string folder = Path.Combine(
                Path.GetTempPath(), "ClipEditorFrames_" + Guid.NewGuid().ToString("N"));

            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(folder);

                    // fps=1/interval gives us roughly ThumbnailCount frames
                    // spread across the whole video in a single pass.
                    double interval = Math.Max(0.04, durationSeconds / ThumbnailCount);
                    string exe = Path.Combine(_ffmpegBinFolder, "ffmpeg.exe");

                    var psi = new ProcessStartInfo(exe,
                        $"-hide_banner -loglevel error -y -i \"{videoPath}\" " +
                        $"-vf \"fps=1/{interval.ToString("0.####", CultureInfo.InvariantCulture)},scale=-2:96\" " +
                        $"-q:v 6 \"{Path.Combine(folder, "frame_%03d.jpg")}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                    };

                    using var proc = Process.Start(psi);
                    proc.WaitForExit();
                }, cts.Token);

                if (cts.IsCancellationRequested)
                    return;

                var frames = new List<System.Windows.Media.ImageSource>();
                foreach (string file in Directory.GetFiles(folder, "frame_*.jpg")
                             .OrderBy(f => f, StringComparer.Ordinal)
                             .Take(ThumbnailCount))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    // OnLoad so the temp file can be deleted straight after.
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(file);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    frames.Add(bitmap);
                }

                if (!cts.IsCancellationRequested && frames.Count > 0)
                    TrimTimeline.SetThumbnails(frames);
            }
            catch
            {
                // A missing filmstrip is cosmetic -- never block playback for it.
            }
            finally
            {
                TryDeleteFolder(folder);
            }
        }

        private static void TryDeleteFolder(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Leftover temp frames are harmless.
            }
        }

        // ----- Caption helpers ---------------------------------------------

        // Makes the caption text safe to embed inside FFmpeg's drawtext
        // filter, which treats colons, backslashes, and quotes specially.
        private static string EscapeForDrawText(string text)
        {
            return text
                .Replace("\\", "\\\\")
                .Replace(":", "\\:")
                .Replace("'", "\u2019"); // swap ' for a look-alike character
        }

        // FFmpeg's drawtext filter doesn't wrap long text on its own -- it
        // just draws one straight line, however long. So if the caption is
        // long enough to likely overflow a vertical (1080px-wide) frame,
        // this splits it into two separate line strings at the space
        // nearest the middle. Each line then gets its OWN drawtext filter
        // in the caller (rather than trying to embed a newline inside one
        // drawtext call, which turned out to be unreliable -- the escape
        // sequence wasn't surviving the trip to FFmpeg intact).
        private static List<string> SplitCaptionIntoLines(string text, bool isVertical)
        {
            if (!isVertical)
                return new List<string> { text }; // only wrapping for the vertical export, as asked

            const double targetWidthPx = 1080.0;
            const double marginPx = 80.0; // leave breathing room on both sides
            const double fontSizePx = 72.0;
            const double avgCharWidthFactor = 0.55; // rough estimate for this font

            double usableWidthPx = targetWidthPx - marginPx;
            int maxCharsPerLine = (int)(usableWidthPx / (fontSizePx * avgCharWidthFactor));

            if (text.Length <= maxCharsPerLine)
                return new List<string> { text };

            // Search outward from the middle of the string for the closest
            // space to break on, so both resulting lines end up roughly
            // balanced instead of one long line and one short one.
            int midpoint = text.Length / 2;
            int breakIndex = -1;

            for (int offset = 0; offset < text.Length; offset++)
            {
                int left = midpoint - offset;
                int right = midpoint + offset;

                if (left >= 0 && text[left] == ' ')
                {
                    breakIndex = left;
                    break;
                }
                if (right < text.Length && text[right] == ' ')
                {
                    breakIndex = right;
                    break;
                }
            }

            if (breakIndex == -1)
                return new List<string> { text }; // no space found (e.g. one very long word)

            string firstLine = text.Substring(0, breakIndex).TrimEnd();
            string secondLine = text.Substring(breakIndex + 1).TrimStart();

            return new List<string> { firstLine, secondLine };
        }

        // Deletes a file and quietly ignores it if that fails (e.g. the
        // file is still briefly locked) rather than crashing the app.
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Not critical if cleanup fails -- just leftover temp files.
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveWindowPlacement();

            foreach (var clip in _clips)
                TryDeleteFile(clip.Path);

            _mediaPlayer.Dispose();
            _libVLC.Dispose();
            base.OnClosed(e);
        }
    }
}
