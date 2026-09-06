using System;
using System.IO;
using System.Text.Json;

namespace ClipEditor
{
    // Small on-disk settings file so paths aren't baked into the build.
    // Lives at %AppData%\ClipEditor\settings.json; every field is optional
    // and the app falls back to a sensible default when one is missing.
    public class AppSettings
    {
        // Default folder the export dialogs open in. Null/empty means "use
        // the EditedClips folder on the desktop".
        public string ExportFolder { get; set; }

        // The folder the last export was actually saved to. Remembered so
        // the dialog reopens where you left off without touching the
        // configured default above.
        public string LastExportFolder { get; set; }

        // Folder containing ffmpeg.exe / ffprobe.exe. Null means "find it on
        // PATH or in a few common install locations".
        public string FfmpegBinFolder { get; set; }

        // Folder the last video was opened from, so the Open dialog reopens
        // there.
        public string LastVideoFolder { get; set; }

        // Selected export preset: 0 = Discord 50 MB, 1 = Discord 20 MB,
        // 2 = full quality (no cap).
        public int PresetIndex { get; set; }

        // Last window placement, restored on the next launch.
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public double WindowLeft { get; set; }
        public double WindowTop { get; set; }
        public bool WindowMaximized { get; set; }

        public static string SettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClipEditor",
                "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(SettingsPath));
                    if (loaded != null)
                        return loaded;
                }
            }
            catch
            {
                // Corrupt or unreadable file -- start from defaults rather
                // than refusing to launch.
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(
                    SettingsPath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Not being able to persist settings shouldn't crash the app.
            }
        }
    }
}
