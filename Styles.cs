using System;
using System.IO;

namespace SpotifyBar
{
    /// <summary>Widget layouts; independent of the colour theme.</summary>
    internal enum WidgetStyle { Pill, Native, Minimal, ProgressFill }

    internal static class Styles
    {
        private static readonly string StyleFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyBar", "style.txt");

        public static WidgetStyle Load()
        {
            try { return Enum.TryParse(File.ReadAllText(StyleFile).Trim(), out WidgetStyle s) ? s : WidgetStyle.Pill; }
            catch { return WidgetStyle.Pill; }
        }

        public static void Save(WidgetStyle style)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StyleFile)!);
                File.WriteAllText(StyleFile, style.ToString());
            }
            catch { }
        }
    }
}
