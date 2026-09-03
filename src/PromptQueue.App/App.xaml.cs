using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PromptQueue.App.ViewModels;
using PromptQueue.App.Views;
using PromptQueue.Core.Storage;

namespace PromptQueue.App;

/// <summary>Interaction logic for App.xaml.</summary>
public partial class App : Application
{
    // ZP-55: only one zProject process may run at a time. A second launch
    // signals the first instance (so it restores / comes to the front) and exits.
    private const string InstanceMutexName = "zProject.SingleInstance.{9C2F1A54-1D3B-4E77-9A0C-7B1E2F6A55D0}";
    private const string ActivateEventName = "zProject.Activate.{9C2F1A54-1D3B-4E77-9A0C-7B1E2F6A55D0}";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;
    private Thread? _activateListener;
    private volatile bool _shuttingDown;
    private bool _startupComplete;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            // Another zProject is already running - wake it and quit.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch
            {
                // best effort; still exit so we never run two processes
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateListener = new Thread(ActivateListenerLoop)
        {
            IsBackground = true,
            Name = "zProject.ActivateListener",
        };
        _activateListener.Start();

        try
        {
            var workspace = Workspace.Load();
            var mainViewModel = new MainViewModel(workspace);

            _mainWindow = new MainWindow { DataContext = mainViewModel };
            _mainWindow.Show();
            _startupComplete = true;

            mainViewModel.WarnAboutLoadErrors();
        }
        catch (Exception ex)
        {
            // Something in startup blew up before a window exists. Show why and
            // exit cleanly — never leave a windowless process holding the
            // single-instance mutex, or every later launch silently no-ops.
            MessageBox.Show(
                $"zProject could not start:\n\n{ex.Message}",
                "zProject", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ActivateListenerLoop()
    {
        var signal = _activateSignal;
        if (signal == null)
            return;

        while (!_shuttingDown)
        {
            try
            {
                if (!signal.WaitOne(500))
                    continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_shuttingDown)
                return;

            Dispatcher.BeginInvoke(new Action(BringMainWindowToFront));
        }
    }

    private void BringMainWindowToFront()
    {
        var window = _mainWindow;
        if (window == null)
            return;

        window.RestoreFromExternalRequest();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        try { _activateSignal?.Set(); } catch { }

        try { _activateListener?.Join(1000); } catch { }

        _activateSignal?.Dispose();
        _activateSignal = null;

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;

        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "zProject",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;

        // If we never finished starting up there is no window to fall back to;
        // swallowing the error would leave a windowless zombie process.
        if (!_startupComplete)
            Shutdown(1);
    }
}
