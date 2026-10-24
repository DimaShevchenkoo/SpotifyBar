using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
        private const string AppName = "SpotifyBar";
        private static readonly TimeSpan WheelStep = TimeSpan.FromSeconds(5);

        private readonly AppSettings _settings = AppSettings.Load();

        private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _popupCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly SpotifyApi _api = new();
        private PlayerState? _player;
        private int? _apiVolume;
        private int _volumeBeforeMute = 50;
        private int? _pendingVolume;
        private bool _sendingVolume, _volumeDragging;
        private DateTime _volumeHoldUntil, _lastApiPoll;
        private string? _title, _artist;
        private WidgetStyle _style = WidgetStyle.Pill;
        private readonly LineBreak _lineBreak = new();

        // Horizontal drag along the taskbar (DragMove would let it leave the bar).
        private double? _dragStartX;
        private double _dragStartLeft;
        private bool _dragging;
        private object? _pressSource;

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private IntPtr _hwnd;
        private IntPtr _hookHandle;
        private uint _taskbarCreatedMsg;
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
            // Explorer broadcasts "TaskbarCreated" when the taskbar appears or restarts; at logon we may
            // start before it exists, so re-measure the taskbar and tray then.
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PlaceWindow);

            Autostart.MigrateFromRunKey();
            AutostartItem.IsChecked = Autostart.IsEnabled();

            Themes.Apply(_settings.Theme);
            UpdateThemeChecks();
            ApplyStyle(_settings.Style);
            _volumeBeforeMute = _settings.VolumeBeforeMute;
            UpdateConnectItem();
            UpdateVolumeUi();
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
            double screenH = SystemParameters.PrimaryScreenHeight;
            double taskbarH = screenH - SystemParameters.WorkArea.Bottom;
            Height = taskbarH >= 32 ? taskbarH : 48;
            Top = screenH - Height;
            // Default: just left of the tray's "show hidden icons" arrow.
            Left = ClampLeft(_settings.Left ?? TrayLeft() - Width - 8);
        }

        /// <summary>Left edge of the tray area (arrow, icons, clock) in DIPs.</summary>
        private double TrayLeft()
        {
            double screenW = SystemParameters.PrimaryScreenWidth;
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            IntPtr tray = taskbar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero || !GetWindowRect(tray, out RECT r) || r.Left <= 0) return screenW - 330;
            return r.Left / VisualTreeHelper.GetDpi(this).DpiScaleX;
        }

        private double ClampLeft(double left) =>
            Math.Clamp(left, 0, Math.Max(0, TrayLeft() - Width - 4));

        private void SavePosition()
        {
            _settings.Left = Left;
            _settings.Save();
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
                bool trackChanged = key != _trackKey;
                if (trackChanged)
                {
                    _trackKey = key;
                    _title = props.Title;
                    _artist = props.Artist;
                    TitleText.Text = string.IsNullOrEmpty(props.Title) ? "Spotify" : props.Title;
                    UpdateArtistText();
                    Root.ToolTip = string.IsNullOrEmpty(props.Artist) ? props.Title : $"{props.Title} — {props.Artist}";
                    await LoadThumbnail(props);
                }
                UpdateProgress();

                // The Web API is only needed for volume and links, so ask it less often.
                if (_api.IsConnected && (trackChanged || DateTime.UtcNow - _lastApiPoll > TimeSpan.FromSeconds(4)))
                    await RefreshPlayer();
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
            SetArt(null);
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

                SetArt(new ImageBrush(bmp) { Stretch = Stretch.UniformToFill });
            }
            catch { }
        }

        private async System.Threading.Tasks.Task RefreshPlayer()
        {
            if (_api.IsConnected)
            {
                _lastApiPoll = DateTime.UtcNow;
                var player = await _api.GetPlayerAsync();
                _player = player;
                if (DateTime.UtcNow > _volumeHoldUntil && !_volumeDragging) _apiVolume = player?.Volume;
                if (_apiVolume > 0) _volumeBeforeMute = _apiVolume.Value;
            }
            if (!_api.IsConnected) // also covers a token Spotify just revoked
            {
                _player = null;
                _apiVolume = null;
            }
            UpdateConnectItem();
            UpdateVolumeUi();
        }

        private void ShowIdle()
        {
            _trackKey = null;
            _title = _artist = null;
            _playing = false;
            _duration = TimeSpan.Zero;
            TitleText.Text = "Spotify не запущено";
            UpdateArtistText();
            Root.ToolTip = null;
            PlayPauseButton.Content = "";
            SetArt(null);
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
                SetProgressVisual(0);
                TimeText.Text = "";
                SquareArt.ToolTip = null;
                return;
            }
            var pos = CurrentPosition();
            TimeText.Text = $"{FormatTime(pos)} / {FormatTime(_duration)}";
            SquareArt.ToolTip = TimeText.Text;
            SetProgressVisual(pos.TotalMilliseconds / _duration.TotalMilliseconds);
        }

        private void SetProgressVisual(double frac)
        {
            ProgressArc.Data = BuildArc(frac);
            Color ring = ((SolidColorBrush)FindResource("RingBrush")).Color;
            Color track = ((SolidColorBrush)FindResource("RingTrackBrush")).Color;
            if (_style == WidgetStyle.Native)
                LinearBar.Background = HardStop(ring, track, frac);
            if (_style == WidgetStyle.ProgressFill)
                ProgressBackground.Background = HardStop(Color.FromArgb(0x30, ring.R, ring.G, ring.B), Colors.Transparent, frac);
        }

        // Left-to-right fill with a sharp edge at frac; needs no layout pass, unlike resizing an element.
        private static Brush HardStop(Color filled, Color rest, double frac)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            brush.GradientStops.Add(new GradientStop(filled, 0));
            brush.GradientStops.Add(new GradientStop(filled, frac));
            brush.GradientStops.Add(new GradientStop(rest, frac));
            brush.GradientStops.Add(new GradientStop(rest, 1));
            brush.Freeze();
            return brush;
        }

        private void SetArt(Brush? cover)
        {
            var fallback = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            Art.Fill = cover ?? fallback;
            SquareArt.Background = cover ?? fallback;
            var glyph = cover == null ? Visibility.Visible : Visibility.Collapsed;
            ArtGlyph.Visibility = glyph;
            SquareGlyph.Visibility = glyph;
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

        // ---------- Volume (Spotify's own, via the Web API) ----------

        private void UpdateVolumeUi()
        {
            int? level = _api.IsConnected ? _apiVolume : null;
            string glyph = level == null || level == 0 ? "\uE74F"
                : level < 34 ? "\uE993"
                : level < 67 ? "\uE994"
                : "\uE995";
            VolumeButton.Content = glyph;
            MuteButton.Content = glyph;
            VolumeButton.Opacity = level == null ? 0.45 : 1;

            double shown = (level ?? 0) / 100.0;
            VolumeText.Text = (level ?? 0).ToString(CultureInfo.InvariantCulture);
            double w = VolumeTrack.Width;
            VolumeFill.Width = w * shown;
            VolumeThumb.Margin = new Thickness((w - VolumeThumb.Width) * shown, 0, 0, 0);
            VolumeButton.ToolTip = VolumePopup.IsOpen ? null
                : !_api.IsConnected ? "Гучність Spotify: натисніть, щоб підключити акаунт"
                : level == null ? "Гучність Spotify (з'явиться, коли Spotify почне грати)"
                : $"Гучність Spotify: {level}%";
        }

        private void SetVolume(double level)
        {
            if (!_api.IsConnected || _apiVolume == null) return;
            int pct = (int)Math.Round(Math.Clamp(level, 0, 1) * 100);
            if (pct > 0) _volumeBeforeMute = pct;
            _apiVolume = pct;
            _volumeHoldUntil = DateTime.UtcNow.AddSeconds(2); // don't let a stale poll undo the change
            UpdateVolumeUi();
            SendVolume(pct);
        }

        // Coalesces slider drags: at most one request in flight, and the last value always wins.
        private async void SendVolume(int pct)
        {
            _pendingVolume = pct;
            if (_sendingVolume) return;
            _sendingVolume = true;
            try
            {
                while (_pendingVolume is int next)
                {
                    _pendingVolume = null;
                    await _api.SetVolumeAsync(next);
                    _volumeHoldUntil = DateTime.UtcNow.AddSeconds(1.5);
                }
            }
            finally
            {
                _sendingVolume = false;
            }
        }

        private async void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_api.IsConnected)
            {
                ShowConnect();
                return;
            }
            VolumePopup.IsOpen = !VolumePopup.IsOpen;
            UpdateVolumeUi();
            if (VolumePopup.IsOpen) await RefreshPlayer();
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            if (_apiVolume is not int level) return;
            if (level > 0)
            {
                // Remember the level across restarts so unmuting later brings it back.
                _settings.VolumeBeforeMute = level;
                _settings.Save();
            }
            SetVolume(level > 0 ? 0 : _volumeBeforeMute / 100.0);
        }

        private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true; // the wheel over the speaker changes volume, not position
            if (_apiVolume is not int level) return;
            int step = e.Delta > 0 ? 5 : -5;
            SetVolume(Math.Round((level + step) / 5.0) * 5 / 100.0);
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

        // ---------- Spotify account and links ----------

        private async void ShowConnect()
        {
            VolumePopup.IsOpen = false;
            var dialog = new ConnectWindow(_api);
            if (dialog.ShowDialog() == true) await RefreshPlayer();
            UpdateConnectItem();
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_api.IsConnected)
            {
                _api.Disconnect();
                _player = null;
                _apiVolume = null;
                UpdateConnectItem();
                UpdateVolumeUi();
            }
            else
            {
                ShowConnect();
            }
        }

        private void UpdateConnectItem() =>
            ConnectItem.Header = _api.IsConnected ? "Відключити акаунт Spotify" : "Підключити акаунт Spotify…";

        private async void OpenTrack()
        {
            if (_title == null)
            {
                SpotifyApi.Open("spotify:"); // nothing playing: just bring Spotify up
                return;
            }
            await RefreshPlayer(); // make sure the IDs belong to the track that's showing
            if (_player?.Item is SpotifyLink item) SpotifyApi.Open(item.Uri);
            else SpotifyApi.Open("spotify:search:" + Uri.EscapeDataString($"{_title} {_artist}".Trim()));
        }

        private async void OpenArtist()
        {
            if (string.IsNullOrEmpty(_artist)) return;
            await RefreshPlayer();
            var artists = _player?.Artists ?? Array.Empty<SpotifyLink>();
            if (artists.Count == 1)
            {
                SpotifyApi.Open(artists[0].Uri);
            }
            else if (artists.Count > 1)
            {
                // Several artists on one track: let the user pick which one to open.
                var menu = new ContextMenu { PlacementTarget = Root, Placement = PlacementMode.Top };
                foreach (var artist in artists)
                {
                    var item = new MenuItem { Header = artist.Name };
                    item.Click += (_, _) => SpotifyApi.Open(artist.Uri);
                    menu.Items.Add(item);
                }
                menu.IsOpen = true;
            }
            else
            {
                SpotifyApi.Open("spotify:search:" + Uri.EscapeDataString(_artist));
            }
        }

        private void Link_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Run run && _title != null) run.TextDecorations = TextDecorations.Underline;
        }

        private void Link_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Run run) run.TextDecorations = null;
        }

        // ---------- Styles ----------

        private void ApplyStyle(WidgetStyle style)
        {
            _style = style;
            bool pill = style == WidgetStyle.Pill, native = style == WidgetStyle.Native;
            bool minimal = style == WidgetStyle.Minimal, fill = style == WidgetStyle.ProgressFill;
            bool twoLines = native || fill;

            Root.CornerRadius = new CornerRadius(pill || minimal ? 18 : 6);
            Root.Height = twoLines ? 40 : 36;
            // Minimal hugs its content from the right so it grows leftwards when the buttons appear.
            Root.HorizontalAlignment = minimal ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            ProgressBackground.CornerRadius = Root.CornerRadius;

            Ring.Visibility = pill ? Visibility.Visible : Visibility.Collapsed;
            SquareArt.Visibility = twoLines ? Visibility.Visible : Visibility.Collapsed;
            MinimalIcon.Visibility = minimal ? Visibility.Visible : Visibility.Collapsed;
            LinearHit.Visibility = native ? Visibility.Visible : Visibility.Collapsed;
            ProgressBackground.Visibility = fill ? Visibility.Visible : Visibility.Collapsed;

            // Two-line styles put the artist under the title.
            TrackLabel.Inlines.Remove(_lineBreak);
            if (twoLines) TrackLabel.Inlines.InsertBefore(ArtistText, _lineBreak);
            ArtistText.FontSize = twoLines ? 11 : 12;
            TrackLabel.MaxWidth = minimal ? 190 : double.PositiveInfinity;
            TrackLabel.Margin = new Thickness(0, 0, minimal ? 10 : 0, 0);

            UpdateArtistText();
            UpdateControlsVisibility();
            UpdateProgress();
            foreach (var item in StyleMenu.Items.OfType<MenuItem>())
                item.IsChecked = (string)item.Tag == style.ToString();
        }

        private void UpdateArtistText()
        {
            bool twoLines = _style is WidgetStyle.Native or WidgetStyle.ProgressFill;
            ArtistText.Text = string.IsNullOrEmpty(_artist) ? "" : twoLines ? _artist : "· " + _artist;
        }

        private void UpdateControlsVisibility()
        {
            bool show = _style != WidgetStyle.Minimal || Root.IsMouseOver || VolumePopup.IsOpen;
            ControlsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Style_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out WidgetStyle style)) return;
            ApplyStyle(style);
            _settings.Style = style;
            _settings.Save();
        }

        private void Root_HoverChanged(object sender, MouseEventArgs e) => UpdateControlsVisibility();

        private void VolumePopup_Closed(object? sender, EventArgs e) => UpdateControlsVisibility();

        private void LinearHit_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // seek, don't drag
            double w = LinearHit.ActualWidth;
            if (w > 0) SeekTo(TimeSpan.FromMilliseconds(_duration.TotalMilliseconds * Math.Clamp(e.GetPosition(LinearHit).X / w, 0, 1)));
        }

        // ---------- Themes ----------

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out Theme theme)) return;
            Themes.Apply(theme);
            _settings.Theme = theme;
            _settings.Save();
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
            _pressSource = e.OriginalSource;
            _dragStartX = CursorX();
            _dragStartLeft = Left;
            _dragging = false;
            Root.CaptureMouse();
            e.Handled = true;
        }

        private void Root_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStartX is not double startX || !Root.IsMouseCaptured) return;
            double dx = (CursorX() - startX) / VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (!_dragging && Math.Abs(dx) < 4) return; // small jitter is still a click
            _dragging = true;
            Root.Cursor = Cursors.SizeWE;
            Left = ClampLeft(_dragStartLeft + dx);
        }

        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragStartX == null) return;
            bool dragged = _dragging;
            Root.ReleaseMouseCapture(); // resets drag state via LostMouseCapture

            // A press that didn't move the widget is a click: on the title or artist it opens them in Spotify.
            if (dragged) SavePosition();
            else if (_pressSource == TitleText) OpenTrack();
            else if (_pressSource == ArtistText) OpenArtist();
        }

        private void Root_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _dragStartX = null;
            _dragging = false;
            Root.Cursor = null;
        }

        private static double CursorX() => GetCursorPos(out POINT p) ? p.X : 0;

        private void ResetPosition_Click(object sender, RoutedEventArgs e)
        {
            _settings.Left = null;
            _settings.Save();
            PlaceWindow();
        }

        private void Autostart_Click(object sender, RoutedEventArgs e)
        {
            if (AutostartItem.IsChecked) Autostart.Enable();
            else Autostart.Disable();
            AutostartItem.IsChecked = Autostart.IsEnabled();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
                Dispatcher.InvokeAsync(PlaceWindow);
            return IntPtr.Zero;
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
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
    }
}
