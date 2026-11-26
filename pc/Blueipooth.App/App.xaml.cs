using System.Threading;
using System.Windows;

namespace Blueipooth.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\Blueipooth_App_SingleInstance";
    private const string ShowEventName = @"Local\Blueipooth_App_ShowWindow";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                string logDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Blueipooth");
                System.IO.Directory.CreateDirectory(logDir);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(logDir, "crash.log"),
                    args.Exception.ToString());
            }
            catch { }
        };

        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            // Signal the already-running instance (e.g. minimized in System Tray) to show its window
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ShowEventName);
                existingEvent.Set();
            }
            catch
            {
                System.Windows.MessageBox.Show(
                    "Blueipooth đang chạy sẵn (kiểm tra biểu tượng ở Khay hệ thống / System Tray góc phải màn hình).",
                    "Blueipooth",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var waitThread = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is MainWindow mw)
                    {
                        mw.Show();
                        mw.WindowState = WindowState.Normal;
                        mw.Activate();
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "Blueipooth-ShowWindowListener"
        };
        waitThread.Start();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
