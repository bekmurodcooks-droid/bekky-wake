using System.Runtime.InteropServices;
using BekkyWake.Core.Core;
using BekkyWake.Core.Store;
using BekkyWake.Core.System;
using BekkyWake.UI;

namespace BekkyWake;

public partial class App : System.Windows.Application
{
    private readonly AppPaths _paths;
    private readonly Mutex _instanceMutex;
    private EventWaitHandle? _showSignal;
    private CancellationTokenSource? _signalCts;

    private WindowsSleepGuard? _sleepGuard;
    private WindowsLidObserver? _lidObserver;
    private SessionManager? _manager;
    private SessionIpcServer? _ipcServer;
    private TrayController? _tray;

    public App(AppPaths paths, Mutex instanceMutex)
    {
        _paths = paths;
        _instanceMutex = instanceMutex;
        InitializeComponent();
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var clock = new SystemClock();
        var log = new RollingLogger(_paths);
        var store = new JsonStore(_paths);
        var recoveryLock = new RecoveryLock(_paths);

        _sleepGuard = new WindowsSleepGuard(log);
        var powerMonitor = new WindowsPowerMonitor();
        _lidObserver = new WindowsLidObserver(_paths, log);
        var display = new WindowsDisplayController();
        var audio = new WindowsAudioController();
        var freezer = new WindowsProcessFreezer();
        var docker = new WindowsDockerController();
        var locker = new WindowsScreenLocker();
        var backstop = new WindowsBackstopScheduler(log);
        var browsers = new WindowsBrowserController();
        var startup = new WindowsStartupManager();

        var lidActions = new LidActions(store, display, audio, freezer, docker, locker, log);

        // Create manager first, then tray (so tray implements INotifier safely).
        var forwardNotifier = new DeferredNotifier();
        _manager = new SessionManager(
            _paths,
            store,
            recoveryLock,
            clock,
            _sleepGuard,
            powerMonitor,
            _lidObserver,
            audio,
            backstop,
            forwardNotifier,
            browsers,
            startup,
            lidActions,
            log);

        _tray = new TrayController(_manager, _paths, clock, freezer, backstop, Dispatcher);
        forwardNotifier.Target = _tray;

        _lidObserver.Start();
        await _manager.ReconcileAsync();

        _ipcServer = new SessionIpcServer(_paths, _manager, clock, log);
        _ipcServer.Start();

        ListenForSecondInstanceActivation();
    }

    private void ListenForSecondInstanceActivation()
    {
        var eventName = _paths.SingleInstanceMutexName + "_Show";
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _signalCts = new CancellationTokenSource();
        var token = _signalCts.Token;

        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_showSignal.WaitOne(500))
                    {
                        _tray?.ShowPopoverNearCursor();
                    }
                }
                catch
                {
                    break;
                }
            }
        }, token);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _signalCts?.Cancel();
        _showSignal?.Dispose();
        _ipcServer?.Dispose();
        _tray?.Dispose();
        _manager?.Dispose();
        _lidObserver?.Dispose();
        _sleepGuard?.Dispose();
        _instanceMutex.ReleaseMutex();
        _instanceMutex.Dispose();
        base.OnExit(e);
    }

    private sealed class DeferredNotifier : INotifier
    {
        public INotifier? Target { get; set; }
        public void Post(string title, string body, bool isWarning = false) =>
            Target?.Post(title, body, isWarning);
    }
}

public static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.FromEnvironment();

        if (args.Length > 0 && string.Equals(args[0], "--session", StringComparison.OrdinalIgnoreCase))
        {
            AttachParentConsoleIfAvailable();
            var store = new JsonStore(paths);
            var clock = new SystemClock();
            var output = SessionCli.ExecuteAsync(args[1..], paths, store, clock).GetAwaiter().GetResult();
            Console.WriteLine(output.Line);
            Console.Out.Flush();
            return output.ExitCode;
        }

        if (args.Length == 1 && string.Equals(args[0], "--backstop", StringComparison.OrdinalIgnoreCase))
        {
            var log = new RollingLogger(paths);
            var store = new JsonStore(paths);
            var lockSvc = new RecoveryLock(paths);
            using var sleepGuard = new WindowsSleepGuard(log);
            var freezer = new WindowsProcessFreezer();
            var runner = new BackstopRunner(store, lockSvc, new SystemClock(), sleepGuard, freezer, log);
            var status = runner.Run();
            return status is BackstopStatus.JournalUnreadable or BackstopStatus.PartialFailure ? 1 : 0;
        }

        if (args.Length == 1 && string.Equals(args[0], "--resume-frozen", StringComparison.OrdinalIgnoreCase))
        {
            var log = new RollingLogger(paths);
            var store = new JsonStore(paths);
            var lockSvc = new RecoveryLock(paths);
            using var sleepGuard = new WindowsSleepGuard(log);
            var freezer = new WindowsProcessFreezer();
            var runner = new BackstopRunner(store, lockSvc, new SystemClock(), sleepGuard, freezer, log);
            return runner.ResumeVerifiedFrozenOnly();
        }

        var mutex = new Mutex(initiallyOwned: true, paths.SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(paths.SingleInstanceMutexName + "_Show");
                evt.Set();
            }
            catch
            {
                // Existing instance may still be starting up.
            }
            mutex.Dispose();
            return 0;
        }

        var app = new App(paths, mutex);
        return app.Run();
    }

    private static void AttachParentConsoleIfAvailable()
    {
        if (!Console.IsOutputRedirected)
        {
            AttachConsole(AttachParentProcess);
        }
    }
}
