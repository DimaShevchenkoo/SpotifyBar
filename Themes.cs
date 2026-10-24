using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpotifyBar
{
    internal enum Theme { Auto, Dark, Light, Clear, Spotify, Accent }

    /// <summary>
    /// Swaps the brushes in App.xaml that every element references via DynamicResource.
    /// </summary>
    internal static class Themes
    {
        public static Theme Current { get; private set; } = Theme.Auto;

        public static void Apply(Theme theme)
        {
            Current = theme;
            bool light = TaskbarIsLight();
            switch (theme)
            {
                case Theme.Dark:
                    SetDark();
                    break;
                case Theme.Light:
                    SetLight();
                    break;
                case Theme.Auto:
                    if (light) SetLight(); else SetDark();
                    break;
                case Theme.Clear:
                    SetDark();
                    Set("PillBrush", 0x01FFFFFF);
                    Set("PillHoverBrush", 0x14FFFFFF);
                    Set("PlayBgBrush", 0x00FFFFFF);
                    Set("PlayFgBrush", 0xFFFFFFFF);
                    Set("RingBrush", 0xE6FFFFFF);
                    break;
                case Theme.Spotify:
                    SetDark();
                    Set("PillBrush", 0x2E1DB954);
                    Set("PillHoverBrush", 0x421DB954);
                    Set("SubTextBrush", 0xFFB9D6C3);
                    Set("PlayBgBrush", 0xFF1DB954);
                    Set("PlayFgBrush", 0xFF000000);
                    Set("RingBrush", 0xFF1ED760);
                    Set("PopupBorderBrush", 0x551DB954);
                    break;
                case Theme.Accent:
                    if (light) SetLight(); else SetDark();
                    uint accent = AccentColor();
                    uint rgb = accent & 0x00FFFFFF;
                    Set("PillBrush", 0x26000000 | rgb);
                    Set("PillHoverBrush", 0x3A000000 | rgb);
                    Set("PlayBgBrush", 0xFF000000 | rgb);
                    Set("PlayFgBrush", IsBright(rgb) ? 0xFF000000 : 0xFFFFFFFF);
                    Set("RingBrush", 0xFF000000 | rgb);
                    break;
            }
        }

        private static void SetDark()
        {
            Set("PillBrush", 0x12FFFFFF);
            Set("PillHoverBrush", 0x1CFFFFFF);
            Set("TextBrush", 0xFFFFFFFF);
            Set("SubTextBrush", 0xFFA8A8A8);
            Set("IconBrush", 0xFFFFFFFF);
            Set("HoverBrush", 0x1FFFFFFF);
            Set("PlayBgBrush", 0xFFFFFFFF);
            Set("PlayFgBrush", 0xFF111111);
            Set("RingBrush", 0xFF1DB954);
            Set("RingTrackBrush", 0x26FFFFFF);
            Set("PopupBrush", 0xFF2C2C2C);
            Set("PopupBorderBrush", 0x1FFFFFFF);
        }

        private static void SetLight()
        {
            Set("PillBrush", 0x0F000000);
            Set("PillHoverBrush", 0x1A000000);
            Set("TextBrush", 0xFF1B1B1B);
            Set("SubTextBrush", 0xFF5C5C5C);
            Set("IconBrush", 0xFF1B1B1B);
            Set("HoverBrush", 0x14000000);
            Set("PlayBgBrush", 0xFF1B1B1B);
            Set("PlayFgBrush", 0xFFFFFFFF);
            Set("RingBrush", 0xFF1DB954);
            Set("RingTrackBrush", 0x26000000);
            Set("PopupBrush", 0xFFF9F9F9);
            Set("PopupBorderBrush", 0x1F000000);
        }

        private static void Set(string key, uint argb)
        {
            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }

        /// <summary>The taskbar follows the "Windows mode" setting, not the app mode.</summary>
        private static bool TaskbarIsLight()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }

        /// <summary>Windows accent colour as 0xRRGGBB (the registry stores it as ABGR).</summary>
        private static uint AccentColor()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                uint c = unchecked((uint)abgr);
                return ((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF);
            }
            return 0x0078D4;
        }

        private static bool IsBright(uint rgb)
        {
            double r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            return 0.299 * r + 0.587 * g + 0.114 * b > 160;
        }
    }
}
