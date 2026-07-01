using System.Threading;
using System.Windows;
using System.Windows.Forms;

namespace BDONightTimeTracker
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // Fixed GUID so the mutex name is stable across builds/machines but
        // unlikely to collide with any other app's named mutex.
        private const string SingleInstanceMutexName = "BDONightTimeTracker-{6E2F7C2A-6B0B-4C0E-9C36-1E4B9E9E3C4B}";

        private Mutex? _singleInstanceMutex;
        private bool   _ownsSingleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            _ownsSingleInstanceMutex = createdNew;

            if (!createdNew)
            {
                NotifyAlreadyRunning();
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Only the instance that actually acquired the mutex owns it and
            // may release it; a second instance never took ownership.
            if (_ownsSingleInstanceMutex)
                _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        // Second instance has no MainWindow (and thus no tray icon) since we
        // shut down before StartupUri creates one, so pop up a throwaway
        // NotifyIcon just long enough to show the balloon tip.
        private static void NotifyAlreadyRunning()
        {
            using var icon = new NotifyIcon
            {
                Icon    = System.Drawing.SystemIcons.Information,
                Visible = true,
            };

            icon.ShowBalloonTip(
                timeout: 3000,
                tipTitle: "BDO Night Tracker",
                tipText: "The widget is already running.",
                tipIcon: ToolTipIcon.Info);

            // Give Windows time to display the balloon before the icon (and
            // process) disappears.
            Thread.Sleep(3000);
        }
    }
}
