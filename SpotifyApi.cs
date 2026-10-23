using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyBar
{
    internal sealed record SpotifyLink(string Name, string Uri);

    internal sealed record PlayerState(int? Volume, string? ItemName, SpotifyLink? Item, IReadOnlyList<SpotifyLink> Artists);

    /// <summary>
    /// Minimal Spotify Web API client for what the media session can't provide:
    /// Spotify's own volume and the IDs of the current track and artists.
    /// Uses Authorization Code + PKCE, so only a public Client ID is needed (no secret).
    /// </summary>
    internal sealed class SpotifyApi
    {
        public const int Port = 8888;
        public const string RedirectUri = "http://127.0.0.1:8888/callback";
        private const string Scopes = "user-read-playback-state user-modify-playback-state";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

        // Refresh token is encrypted for the current Windows user (DPAPI).
        private readonly string _file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyBar", "spotify.dat");

        private string? _clientId;
        private string? _refreshToken;
        private string? _accessToken;
        private DateTime _accessExpires;

        public SpotifyApi() => Load();

        public bool IsConnected => _clientId != null && _refreshToken != null;
        public string? ClientId => _clientId;

        // ---------- Login ----------

        public async Task ConnectAsync(string clientId, CancellationToken ct)
        {
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomNumberGenerator.GetBytes(16));

            string authUrl = "https://accounts.spotify.com/authorize"
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(clientId)
                + "&scope=" + Uri.EscapeDataString(Scopes)
                + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                + "&code_challenge_method=S256&code_challenge=" + challenge
                + "&state=" + state;

            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            try
            {
                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
                string code = await WaitForCallbackAsync(listener, state, ct);

                var json = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = clientId,
                    ["code_verifier"] = verifier,
                }, ct);

                _clientId = clientId;
                ReadTokens(json);
                Save();
            }
            finally
            {
                listener.Stop();
            }
        }

        public void Disconnect()
        {
            _refreshToken = null;
            _accessToken = null;
            try { File.Delete(_file); } catch { }
        }

        private static async Task<string> WaitForCallbackAsync(TcpListener listener, string expectedState, CancellationToken ct)
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                // "GET /callback?code=...&state=... HTTP/1.1"
                string? requestLine = await reader.ReadLineAsync(ct);
                string target = requestLine?.Split(' ').ElementAtOrDefault(1) ?? "";
                if (!target.StartsWith("/callback", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 404, "", ct);
                    continue; // e.g. /favicon.ico
                }

                var query = ParseQuery(target);
                query.TryGetValue("error", out string? error);
                query.TryGetValue("code", out string? code);
                query.TryGetValue("state", out string? state);

                bool ok = error == null && code != null && state == expectedState;
                await RespondAsync(stream, 200, ok
                    ? "<h2>SpotifyBar connected</h2><p>You can close this tab.</p>"
                    : "<h2>SpotifyBar: sign-in failed</h2><p>Go back to SpotifyBar and try again.</p>", ct);

                if (error != null) throw new InvalidOperationException(error == "access_denied" ? "Вхід скасовано." : error);
                if (state != expectedState) throw new InvalidOperationException("Невірна відповідь від Spotify.");
                if (code == null) throw new InvalidOperationException("Spotify не повернув код входу.");
                return code;
            }
        }

        private static async Task RespondAsync(Stream stream, int status, string body, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(
                "<!doctype html><meta charset=utf-8><body style=\"font-family:Segoe UI,sans-serif;background:#121212;color:#fff;padding:40px\">"
                + body + "</body>");
            string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n"
                + "Content-Type: text/html; charset=utf-8\r\n"
                + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(bytes, ct);
        }

        private static Dictionary<string, string> ParseQuery(string target)
        {
            var result = new Dictionary<string, string>();
            int q = target.IndexOf('?');
            if (q < 0) return result;
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0) result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return result;
        }

        // ---------- Tokens ----------

        private async Task<string?> AccessTokenAsync()
        {
            if (!IsConnected) return null;
            if (_accessToken != null && DateTime.UtcNow < _accessExpires) return _accessToken;
            try
            {
                var json = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _refreshToken!,
                    ["client_id"] = _clientId!,
                }, CancellationToken.None);
                ReadTokens(json);
                Save(); // Spotify may rotate the refresh token
                return _accessToken;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                Disconnect(); // refresh token revoked or app deleted
                return null;
            }
            catch
            {
                return null; // offline; try again on the next poll
            }
        }

        private static async Task<JsonNode> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
        {
            using var response = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                string? reason = JsonNode.Parse(body)?["error_description"]?.GetValue<string>();
                throw new HttpRequestException(reason ?? $"Spotify відповів {(int)response.StatusCode}.", null, response.StatusCode);
            }
            return JsonNode.Parse(body)!;
        }

        private void ReadTokens(JsonNode json)
        {
            _accessToken = json["access_token"]!.GetValue<string>();
            _accessExpires = DateTime.UtcNow.AddSeconds(json["expires_in"]!.GetValue<int>() - 60);
            if (json["refresh_token"]?.GetValue<string>() is string refresh) _refreshToken = refresh;
        }

        private void Load()
        {
            try
            {
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_file), null, DataProtectionScope.CurrentUser);
                var json = JsonNode.Parse(plain)!;
                _clientId = json["clientId"]?.GetValue<string>();
                _refreshToken = json["refreshToken"]?.GetValue<string>();
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new { clientId = _clientId, refreshToken = _refreshToken });
                File.WriteAllBytes(_file, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            }
            catch { }
        }

        // ---------- Player ----------

        /// <summary>Current Spotify player, or null when nothing is active or the request failed.</summary>
        public async Task<PlayerState?> GetPlayerAsync()
        {
            string? token = await AccessTokenAsync();
            if (token == null) return null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player?additional_types=episode");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await Http.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode) return null;

                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                int? volume = json["device"]?["volume_percent"]?.GetValue<int?>();
                var item = json["item"];
                string? name = item?["name"]?.GetValue<string>();
                SpotifyLink? link = name != null && item?["uri"]?.GetValue<string>() is string uri ? new(name, uri) : null;

                // Tracks have artists; podcast episodes have a show instead.
                var artists = item?["artists"]?.AsArray()
                    .Select(a => new SpotifyLink(a!["name"]!.GetValue<string>(), a["uri"]!.GetValue<string>()))
                    .ToList() ?? new List<SpotifyLink>();
                if (artists.Count == 0 && item?["show"] is JsonNode show)
                    artists.Add(new SpotifyLink(show["name"]!.GetValue<string>(), show["uri"]!.GetValue<string>()));

                return new PlayerState(volume, name, link, artists);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Sets Spotify's own volume (Premium only). Returns false on failure.</summary>
        public async Task<bool> SetVolumeAsync(int percent)
        {
            string? token = await AccessTokenAsync();
            if (token == null) return false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put,
                    $"https://api.spotify.com/v1/me/player/volume?volume_percent={Math.Clamp(percent, 0, 100)}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await Http.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public static void Open(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
