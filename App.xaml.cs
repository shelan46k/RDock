using System.Threading;
using System.Windows;

namespace RDock;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Local\RDock.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "RDock 已在執行中。",
                "RDock",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnLastWindowClose;
        await DockManager.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // ignore
        }

        base.OnExit(e);
    }
}
