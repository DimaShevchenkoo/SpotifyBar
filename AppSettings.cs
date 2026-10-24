using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotifyBar
{
    /// <summary>
    /// Everything the widget remembers between runs, in one JSON file. Saved right after each change;
    /// the Spotify sign-in stays separate in spotify.dat because it's encrypted.
    /// </summary>
    internal sealed class AppSettings
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyBar");
        private static readonly string FilePath = Path.Combine(Dir, "settings.json");

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public Theme Theme { get; set; } = Theme.Auto;
        public WidgetStyle Style { get; set; } = WidgetStyle.Pill;

        /// <summary>Widget's left edge on the taskbar in DIPs; null means "just left of the tray".</summary>
        public double? Left { get; set; }

        /// <summary>Spotify volume to restore when unmuting from the widget.</summary>
        public int VolumeBeforeMute { get; set; } = 50;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            }
            catch
            {
                // Corrupt file: fall through to defaults rather than refusing to start.
                return new AppSettings();
            }

            var migrated = MigrateLegacyFiles();
            migrated.Save();
            return migrated;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                // Write to a temp file and swap it in, so a crash or power loss mid-write can't leave a half-written file.
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch { }
        }

        /// <summary>Earlier versions kept each option in its own text file.</summary>
        private static AppSettings MigrateLegacyFiles()
        {
            var settings = new AppSettings();
            string? Read(string name)
            {
                try { return File.ReadAllText(Path.Combine(Dir, name)).Trim(); }
                catch { return null; }
            }

            if (Enum.TryParse(Read("theme.txt"), out Theme theme)) settings.Theme = theme;
            if (Enum.TryParse(Read("style.txt"), out WidgetStyle style)) settings.Style = style;
            if (Read("position.txt")?.Split(';')[0] is string left
                && double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                settings.Left = x;

            foreach (var name in new[] { "theme.txt", "style.txt", "position.txt" })
                try { File.Delete(Path.Combine(Dir, name)); } catch { }
            return settings;
        }
    }
}
