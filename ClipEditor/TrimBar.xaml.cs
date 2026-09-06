using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;

namespace ClipEditor
{
    // A single-track timeline: one playhead plus draggable in/out handles
    // with a shaded selection band between them. All positions are in
    // seconds; the control maps them to pixels from its own width.
    public partial class TrimBar : UserControl
    {
        private const double EdgePaddingPx = 8.0;   // keeps handles inside the control
        private const double MinGapSeconds = 0.05;  // in and out can't collapse onto each other
        private const double StripTopPx = 2.0;      // filmstrip band, matches TrimBar.xaml
        private const double StripHeightPx = 46.0;

        // Frame thumbnails drawn behind the track, laid out left to right
        // across the full duration.
        private readonly List<Image> _thumbnailImages = new List<Image>();

        public TrimBar()
        {
            InitializeComponent();
            Loaded += (s, e) => UpdateVisual();
        }

        // Raised when the user moves the playhead (track click or playhead drag).
        public event EventHandler<double> SeekRequested;

        // Raised continuously while an in/out handle is being dragged, so the
        // host can scrub the preview to that frame.
        public event EventHandler<double> ScrubPreview;

        // Raised whenever InPoint or OutPoint changes from user interaction.
        public event EventHandler InOutChanged;

        // True while any handle is being dragged -- the host should stop
        // pushing Position updates from its playback timer during that time.
        public bool IsUserDragging { get; private set; }

        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register(nameof(Duration), typeof(double), typeof(TrimBar),
                new PropertyMetadata(0.0, OnVisualPropertyChanged));

        public static readonly DependencyProperty PositionProperty =
            DependencyProperty.Register(nameof(Position), typeof(double), typeof(TrimBar),
                new PropertyMetadata(0.0, OnVisualPropertyChanged));

        public static readonly DependencyProperty InPointProperty =
            DependencyProperty.Register(nameof(InPoint), typeof(double), typeof(TrimBar),
                new PropertyMetadata(0.0, OnVisualPropertyChanged));

        public static readonly DependencyProperty OutPointProperty =
            DependencyProperty.Register(nameof(OutPoint), typeof(double), typeof(TrimBar),
                new PropertyMetadata(0.0, OnVisualPropertyChanged));

        public double Duration
        {
            get => (double)GetValue(DurationProperty);
            set => SetValue(DurationProperty, value);
        }

        public double Position
        {
            get => (double)GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        public double InPoint
        {
            get => (double)GetValue(InPointProperty);
            set => SetValue(InPointProperty, value);
        }

        public double OutPoint
        {
            get => (double)GetValue(OutPointProperty);
            set => SetValue(OutPointProperty, value);
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((TrimBar)d).UpdateVisual();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateVisual();

        // ----- Geometry -------------------------------------------------------

        private double TrackLeftPx => EdgePaddingPx;

        private double TrackWidthPx => Math.Max(1.0, ActualWidth - 2 * EdgePaddingPx);

        private double SecondsToX(double seconds)
        {
            double frac = Duration <= 0 ? 0 : Clamp(seconds / Duration, 0, 1);
            return TrackLeftPx + frac * TrackWidthPx;
        }

        private double XToSeconds(double x)
        {
            double frac = Clamp((x - TrackLeftPx) / TrackWidthPx, 0, 1);
            return frac * Duration;
        }

        private double PixelsToSeconds(double px) => Duration <= 0 ? 0 : px / TrackWidthPx * Duration;

        private void UpdateVisual()
        {
            if (ActualWidth <= 0)
                return;

            Canvas.SetLeft(Track, TrackLeftPx);
            Track.Width = TrackWidthPx;

            double inX = SecondsToX(InPoint);
            double outX = SecondsToX(OutPoint);
            double headX = SecondsToX(Position);

            Canvas.SetLeft(SelectionBand, inX);
            SelectionBand.Width = Math.Max(0, outX - inX);

            Canvas.SetLeft(InThumb, inX - InThumb.Width / 2);
            Canvas.SetLeft(OutThumb, outX - OutThumb.Width / 2);
            Canvas.SetLeft(PlayheadThumb, headX - PlayheadThumb.Width / 2);

            LayoutThumbnails();

            // Shade the filmstrip outside the selection so the kept range reads
            // at a glance.
            Canvas.SetLeft(DimLeft, TrackLeftPx);
            DimLeft.Width = Math.Max(0, inX - TrackLeftPx);

            Canvas.SetLeft(DimRight, outX);
            DimRight.Width = Math.Max(0, TrackLeftPx + TrackWidthPx - outX);
        }

        // Spreads the frames evenly across the full width of the track. Each
        // cell is clipped so a frame with a different aspect ratio crops
        // instead of bleeding into its neighbours.
        private void LayoutThumbnails()
        {
            if (_thumbnailImages.Count == 0)
                return;

            double cellWidth = TrackWidthPx / _thumbnailImages.Count;

            for (int i = 0; i < _thumbnailImages.Count; i++)
            {
                Image image = _thumbnailImages[i];

                // The half pixel of overlap hides seams between cells.
                image.Width = cellWidth + 0.5;
                image.Height = StripHeightPx;
                image.Clip = new RectangleGeometry(new Rect(0, 0, image.Width, image.Height));

                Canvas.SetLeft(image, TrackLeftPx + i * cellWidth);
                Canvas.SetTop(image, StripTopPx);
            }
        }

        // ----- Interaction --------------------------------------------------

        private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Duration <= 0)
                return;

            double seconds = XToSeconds(e.GetPosition(RootCanvas).X);
            Position = seconds;
            SeekRequested?.Invoke(this, seconds);
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e) => IsUserDragging = true;

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e) => IsUserDragging = false;

        private void InThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (Duration <= 0)
                return;

            double next = Clamp(InPoint + PixelsToSeconds(e.HorizontalChange),
                0, OutPoint - MinGapSeconds);

            InPoint = next;
            ScrubPreview?.Invoke(this, next);
            InOutChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OutThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (Duration <= 0)
                return;

            double next = Clamp(OutPoint + PixelsToSeconds(e.HorizontalChange),
                InPoint + MinGapSeconds, Duration);

            OutPoint = next;
            ScrubPreview?.Invoke(this, next);
            InOutChanged?.Invoke(this, EventArgs.Empty);
        }

        private void PlayheadThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (Duration <= 0)
                return;

            double next = Clamp(Position + PixelsToSeconds(e.HorizontalChange), 0, Duration);
            Position = next;
            SeekRequested?.Invoke(this, next);
        }

        // Replaces the filmstrip behind the track. Pass an empty list to
        // clear it (e.g. while a new video is still being scanned).
        public void SetThumbnails(IReadOnlyList<ImageSource> frames)
        {
            foreach (var existing in _thumbnailImages)
                RootCanvas.Children.Remove(existing);
            _thumbnailImages.Clear();

            foreach (var frame in frames)
            {
                var image = new Image
                {
                    Source = frame,
                    Stretch = Stretch.UniformToFill,
                    IsHitTestVisible = false,
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);

                // Index 0 keeps them underneath the track, handles and dimming.
                RootCanvas.Children.Insert(0, image);
                _thumbnailImages.Add(image);
            }

            UpdateVisual();
        }

        private static double Clamp(double value, double min, double max)
            => value < min ? min : (value > max ? max : value);
    }
}
