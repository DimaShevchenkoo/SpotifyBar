using System.Threading;
using System.Windows;

namespace SpotifyBar
{
    public partial class App : Application
    {
        private Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, "SpotifyBar.SingleInstance", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }
            base.OnStartup(e);
        }
    }
}
