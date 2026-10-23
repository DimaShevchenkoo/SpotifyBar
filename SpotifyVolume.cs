using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace SpotifyBar
{
    /// <summary>
    /// Spotify's own volume isn't exposed through the media session, so this drives
    /// Spotify's slider in the Windows volume mixer instead.
    /// </summary>
    internal sealed class SpotifyVolume
    {
        private readonly MMDeviceEnumerator _enumerator = new();
        private SimpleAudioVolume? _volume;

        public bool IsAvailable => _volume != null;

        /// <summary>Re-finds Spotify's audio session; it changes when the output device or Spotify restarts.</summary>
        public void Refresh()
        {
            _volume = null;
            try
            {
                var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.GetProcessID != 0 && IsSpotify((int)session.GetProcessID))
                    {
                        _volume = session.SimpleAudioVolume;
                        return;
                    }
                }
            }
            catch
            {
                // No audio device, or the audio service is restarting.
            }
        }

        /// <summary>0..1, or null when Spotify has no audio session yet.</summary>
        public float? Level
        {
            get
            {
                try { return _volume == null ? null : _volume.Volume; }
                catch { _volume = null; return null; }
            }
            set
            {
                if (_volume == null || value == null) return;
                try
                {
                    _volume.Volume = Math.Clamp(value.Value, 0f, 1f);
                    if (value > 0 && _volume.Mute) _volume.Mute = false;
                }
                catch { _volume = null; }
            }
        }

        public bool Muted
        {
            get
            {
                try { return _volume?.Mute ?? false; }
                catch { _volume = null; return false; }
            }
            set
            {
                try { if (_volume != null) _volume.Mute = value; }
                catch { _volume = null; }
            }
        }

        private static bool IsSpotify(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
