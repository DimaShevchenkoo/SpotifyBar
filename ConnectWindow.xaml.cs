using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

namespace SpotifyBar
{
    public partial class ConnectWindow : Window
    {
        private readonly SpotifyApi _api;
        private CancellationTokenSource? _login;
        private bool _closed;

        internal ConnectWindow(SpotifyApi api)
        {
            InitializeComponent();
            _api = api;
            RedirectText.Text = SpotifyApi.RedirectUri;
            ClientIdBox.Text = api.ClientId ?? "";
        }

        private static bool LooksLikeClientId(string s) => Regex.IsMatch(s, "^[0-9a-fA-F]{32}$");

        private void ClientIdBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            ConnectButton.IsEnabled = LooksLikeClientId(ClientIdBox.Text.Trim());

        private void OpenDashboard_Click(object sender, RoutedEventArgs e) =>
            SpotifyApi.Open("https://developer.spotify.com/dashboard");

        private void CopyRedirect_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(SpotifyApi.RedirectUri); } catch { }
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsEnabled = false;
            ShowStatus("Увійдіть у Spotify у браузері, що відкрився, і натисніть «Agree»…");
            _login = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await _api.ConnectAsync(ClientIdBox.Text.Trim(), _login.Token);
                if (!_closed) DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Час на вхід вийшов. Спробуйте ще раз.");
            }
            catch (System.Net.Sockets.SocketException)
            {
                ShowStatus($"Порт {SpotifyApi.Port} зайнятий іншою програмою. Закрийте її і спробуйте ще раз.");
            }
            catch (Exception ex)
            {
                ShowStatus("Не вдалося підключитись: " + ex.Message);
            }
            finally
            {
                ConnectButton.IsEnabled = LooksLikeClientId(ClientIdBox.Text.Trim());
                if (!_closed && DialogResult != true) Activate();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _login?.Cancel();
            DialogResult = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _login?.Cancel();
            base.OnClosed(e);
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
