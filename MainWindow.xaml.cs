using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;

namespace SpotifyBar
{
    public partial class MainWindow : Window
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "SpotifyBar";
        private static readonly TimeSpan WheelStep = TimeSpan.FromSeconds(5);

        private readonly string _posFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "position.txt");

        private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _popupCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly SpotifyVolume _volume = new();
        private bool _volumeDragging;

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private IntPtr _hwnd;
        private IntPtr _hookHandle;
        private WinEventProc? _foregroundHook; // kept in a field so the GC doesn't collect the callback
        private bool _hiddenForFullscreen;
        private bool _polling;
        private string? _trackKey;

        // Spotify only reports position occasionally, so interpolate between reports.
        private TimeSpan _position, _duration;
        private DateTimeOffset _positionStamp;
        private bool _playing;

        public MainWindow()
        {
            InitializeComponent();

            // Centre the volume popup above the speaker button.
            VolumePopup.CustomPopupPlacementCallback = (popup, target, _) => new[]
            {
                new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, -popup.Height - 8), PopupPrimaryAxis.Horizontal),
            };
            _popupCloseTimer.Tick += (_, _) =>
            {
                _popupCloseTimer.Stop();
                if (!VolumeButton.IsMouseOver && !VolumePanel.IsMouseOver && !_volumeDragging)
                    VolumePopup.IsOpen = false;
            };
        }

        protected override async void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;

            // Tool window: hidden from Alt+Tab. No-activate: clicks don't steal focus.
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            PlaceWindow();
            AutostartItem.IsChecked = IsAutostartEnabled();

            Themes.Apply(Themes.Load());
            UpdateThemeChecks();
            // Auto and accent themes follow Windows settings live.
            SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                if (Themes.Current is Theme.Auto or Theme.Accent) Themes.Apply(Themes.Current);
            });

            _uiTimer.Tick += (_, _) => { KeepOnTop(); UpdateProgress(); };
            _pollTimer.Tick += async (_, _) => await Poll();
            _uiTimer.Start();
            _pollTimer.Start();

            // React the moment focus moves (Win key, tray flyout, leaving a fullscreen app)
            // instead of waiting for the next timer tick.
            _foregroundHook = OnForegroundChanged;
            _hookHandle = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundHook, 0, 0, WINEVENT_OUTOFCONTEXT);

            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            await Poll();
        }

        // ---------- Positioning ----------

        private void PlaceWindow()
        {
            double screenW = SystemParameters.PrimaryScreenWidth;
            double screenH = SystemParameters.PrimaryScreenHeight;
            double taskbarH = screenH - SystemParameters.WorkArea.Bottom;
            Height = taskbarH >= 32 ? taskbarH : 48;

            if (TryLoadPosition(out double left, out double top))
            {
                Left = left;
                Top = top;
                return;
            }
            // Default: on the taskbar, to the left of the system tray.
            Left = screenW - Width - 330;
            Top = screenH - Height;
        }

        private bool TryLoadPosition(out double left, out double top)
        {
            left = top = 0;
            try
            {
                var parts = File.ReadAllText(_posFile).Split(';');
                return parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top)
                    && left > SystemParameters.VirtualScreenLeft - Width / 2
                    && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width / 2
                    && top > SystemParameters.VirtualScreenTop - Height / 2
                    && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height / 2;
            }
            catch
            {
                return false;
            }
        }

        private void SavePosition()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_posFile)!);
                File.WriteAllText(_posFile, string.Create(CultureInfo.InvariantCulture, $"{Left};{Top}"));
            }
            catch { }
        }

        private async void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            KeepOnTop();
            // The taskbar finishes raising itself a moment after focus moves; follow it up.
            await System.Threading.Tasks.Task.Delay(120);
            KeepOnTop();
            await System.Threading.Tasks.Task.Delay(300);
            KeepOnTop();
        }

        private void KeepOnTop()
        {
            // The taskbar re-raises itself when clicked; push back above it.
            // Hide while a fullscreen app (game, video) is in front. Raw Win32 show/hide
            // so showing again never activates the widget.
            bool hide = IsForegroundFullscreen();
            if (hide != _hiddenForFullscreen)
            {
                _hiddenForFullscreen = hide;
                ShowWindow(_hwnd, hide ? SW_HIDE : SW_SHOWNOACTIVATE);
            }
            if (!hide)
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_hookHandle != IntPtr.Zero) UnhookWinEvent(_hookHandle);
            base.OnClosed(e);
        }

        private bool IsForegroundFullscreen()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _hwnd || fg == GetShellWindow()) return false;

            var cls = new System.Text.StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;

            if (!GetWindowRect(fg, out RECT r)) return false;
            IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            bool coversMonitor = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
                && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;

            // Start, search, tray flyouts and quick settings are invisible monitor-sized
            // windows; they mean the taskbar is showing, not that a game is.
            return coversMonitor && !IsShellProcess(fg);
        }

        private static readonly string[] ShellProcesses =
        {
            "explorer", "StartMenuExperienceHost", "ShellExperienceHost", "ShellHost",
            "SearchHost", "SearchApp", "TextInputHost", "LockApp",
        };

        private static bool IsShellProcess(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return ShellProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ---------- Spotify state ----------

        private async System.Threading.Tasks.Task Poll()
        {
            if (_manager == null || _polling) return;
            _polling = true;
            try
            {
                if (!_volumeDragging)
                {
                    _volume.Refresh();
                    UpdateVolumeUi();
                }

                _session = _manager.GetSessions().FirstOrDefault(s =>
                    s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));

                if (_session == null)
                {
                    ShowIdle();
                    return;
                }

                var playback = _session.GetPlaybackInfo();
                _playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlayPauseButton.Content = _playing ? "" : "";

                var tl = _session.GetTimelineProperties();
                _duration = tl.EndTime - tl.StartTime;
                _position = tl.Position;
                _positionStamp = tl.LastUpdatedTime;

                var props = await _session.TryGetMediaPropertiesAsync();
                string key = props.Title + "\u0001" + props.Artist;
                if (key != _trackKey)
                {
                    _trackKey = key;
                    TitleText.Text = string.IsNullOrEmpty(props.Title) ? "Spotify" : props.Title;
                    ArtistText.Text = string.IsNullOrEmpty(props.Artist) ? "" : "· " + props.Artist;
                    Root.ToolTip = string.IsNullOrEmpty(props.Artist) ? props.Title : $"{props.Title} — {props.Artist}";
                    await LoadThumbnail(props);
                }
                UpdateProgress();
            }
            catch
            {
                // Session can vanish mid-call when Spotify closes; next poll recovers.
            }
            finally
            {
                _polling = false;
            }
        }

        private async System.Threading.Tasks.Task LoadThumbnail(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            Art.Fill = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            ArtGlyph.Visibility = Visibility.Visible;
            if (props.Thumbnail == null) return;
            try
            {
                using var winStream = await props.Thumbnail.OpenReadAsync();
                using var stream = winStream.AsStreamForRead();
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                Art.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                ArtGlyph.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void ShowIdle()
        {
            _trackKey = null;
            _playing = false;
            _duration = TimeSpan.Zero;
            TitleText.Text = "Spotify не запущено";
            ArtistText.Text = "";
            Root.ToolTip = null;
            PlayPauseButton.Content = "";
            Art.Fill = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            ArtGlyph.Visibility = Visibility.Visible;
            UpdateProgress();
        }

        private TimeSpan CurrentPosition()
        {
            var pos = _position;
            if (_playing && _positionStamp != default)
                pos += DateTimeOffset.Now - _positionStamp;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            if (pos > _duration) pos = _duration;
            return pos;
        }

        private void UpdateProgress()
        {
            if (_duration <= TimeSpan.Zero)
            {
                ProgressArc.Data = null;
                TimeText.Text = "";
                return;
            }
            var pos = CurrentPosition();
            TimeText.Text = $"{FormatTime(pos)} / {FormatTime(_duration)}";
            ProgressArc.Data = BuildArc(pos.TotalMilliseconds / _duration.TotalMilliseconds);
        }

        private static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        // Clockwise arc from 12 o'clock covering `frac` of the ring.
        private Geometry? BuildArc(double frac)
        {
            double size = Ring.Width, stroke = ProgressArc.StrokeThickness;
            double c = size / 2, r = c - stroke / 2;
            if (frac <= 0.001) return null;
            if (frac >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);

            double a = 2 * Math.PI * frac;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(c, c - r), false, false);
                ctx.ArcTo(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0,
                    frac > 0.5, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            return geo;
        }

        private async void SeekTo(TimeSpan target)
        {
            if (_session == null || _duration <= TimeSpan.Zero) return;
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (target > _duration) target = _duration;

            // Optimistic update so the bar jumps immediately.
            _position = target;
            _positionStamp = DateTimeOffset.Now;
            UpdateProgress();
            try { await _session.TryChangePlaybackPositionAsync(target.Ticks); } catch { }
        }

        // ---------- Volume ----------

        private void UpdateVolumeUi()
        {
            float? level = _volume.Level;
            bool muted = _volume.Muted;
            string glyph = level == null || muted || level < 0.005f ? ""
                : level < 0.34f ? ""
                : level < 0.67f ? ""
                : "";
            VolumeButton.Content = glyph;
            MuteButton.Content = glyph;
            VolumeButton.Opacity = level == null ? 0.45 : 1;

            double shown = muted ? 0 : level ?? 0;
            int pct = (int)Math.Round(shown * 100);
            VolumeText.Text = pct.ToString(CultureInfo.InvariantCulture);
            double w = VolumeTrack.Width;
            VolumeFill.Width = w * shown;
            VolumeThumb.Margin = new Thickness((w - VolumeThumb.Width) * shown, 0, 0, 0);
            VolumeButton.ToolTip = VolumePopup.IsOpen ? null
                : level == null ? "Гучність Spotify (з'явиться, коли Spotify почне грати)"
                : $"Гучність Spotify: {pct}%";
        }

        private void SetVolume(double level)
        {
            if (!_volume.IsAvailable) return;
            _volume.Level = (float)Math.Clamp(level, 0, 1);
            UpdateVolumeUi();
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            _volume.Refresh();
            VolumePopup.IsOpen = !VolumePopup.IsOpen;
            UpdateVolumeUi();
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            _volume.Muted = !_volume.Muted;
            UpdateVolumeUi();
        }

        private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true; // the wheel over the speaker changes volume, not position
            if (_volume.Level is not float level) return;
            double step = e.Delta > 0 ? 0.05 : -0.05;
            SetVolume(Math.Round((level + step) * 20) / 20);
        }

        private void VolumeTrack_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _volumeDragging = VolumeTrack.CaptureMouse();
            SetVolumeFromPointer(e);
        }

        private void VolumeTrack_MouseMove(object sender, MouseEventArgs e)
        {
            if (_volumeDragging) SetVolumeFromPointer(e);
        }

        private void VolumeTrack_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _volumeDragging = false;
            VolumeTrack.ReleaseMouseCapture();
            if (!VolumePanel.IsMouseOver) _popupCloseTimer.Start();
        }

        private void SetVolumeFromPointer(MouseEventArgs e)
        {
            double half = VolumeThumb.Width / 2;
            SetVolume((e.GetPosition(VolumeTrack).X - half) / (VolumeTrack.Width - 2 * half));
        }

        private void VolumePanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // no window drag from the popup

        private void Volume_MouseEnter(object sender, MouseEventArgs e) => _popupCloseTimer.Stop();

        private void Volume_MouseLeave(object sender, MouseEventArgs e)
        {
            if (VolumePopup.IsOpen) _popupCloseTimer.Start();
        }

        // ---------- Themes ----------

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out Theme theme)) return;
            Themes.Apply(theme);
            Themes.Save(theme);
            UpdateThemeChecks();
        }

        private void UpdateThemeChecks()
        {
            foreach (var item in ThemeMenu.Items.OfType<MenuItem>())
                item.IsChecked = (string)item.Tag == Themes.Current.ToString();
        }

        // ---------- Input ----------

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            try { await _session.TryTogglePlayPauseAsync(); } catch { }
            await Poll();
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            try { await _session.TrySkipNextAsync(); } catch { }
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            try { await _session.TrySkipPreviousAsync(); } catch { }
        }

        private void Ring_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // don't start a window drag
            // Angle clockwise from 12 o'clock → fraction of the track.
            var p = e.GetPosition(Ring);
            double c = Ring.Width / 2;
            double angle = Math.Atan2(p.X - c, c - p.Y);
            if (angle < 0) angle += 2 * Math.PI;
            SeekTo(TimeSpan.FromMilliseconds(_duration.TotalMilliseconds * angle / (2 * Math.PI)));
        }

        private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            SeekTo(CurrentPosition() + (e.Delta > 0 ? WheelStep : -WheelStep));
        }

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
            SavePosition();
        }

        private void ResetPosition_Click(object sender, RoutedEventArgs e)
        {
            try { File.Delete(_posFile); } catch { }
            PlaceWindow();
        }

        private void Autostart_Click(object sender, RoutedEventArgs e)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (AutostartItem.IsChecked)
                key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
        }

        private static bool IsAutostartEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        // ---------- Win32 ----------

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);

        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    }
}
