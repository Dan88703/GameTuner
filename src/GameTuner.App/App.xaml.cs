using GameTuner.App.Services;
using GameTuner.App.ViewModels;
using GameTuner.Core;
using GameTuner.Core.Automation;
using GameTuner.Core.Monitoring;
using GameTuner.Core.Settings;
using GameTuner.Core.Tuning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
// Serilog's sink methods (WriteTo.File) are extension methods: they are only
// visible with the namespace imported, a fully qualified type name is not enough.
using Serilog;

namespace GameTuner.App;

public partial class App : Application
{
    /// <summary>Command-line flag used after "restart as admin".</summary>
    public const string ElevatedRestartArg = "--elevated-restart";

    private const string MutexName = @"Local\GameTuner.SingleInstance";
    private const string ShowEventName = @"Local\GameTuner.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private ILogger<App>? _log;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        _log = Services.GetRequiredService<ILogger<App>>();

        // Last line of defence: log crashes instead of disappearing silently.
        UnhandledException += (_, e) =>
        {
            _log.LogCritical(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
    }

    /// <summary>The DI container. Pages use it to get their view models.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static MainWindow? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] cmd = Environment.GetCommandLineArgs();

        if (!AcquireSingleInstance(waitForPrevious: cmd.Contains(ElevatedRestartArg)))
        {
            // Another GameTuner is running: ask it to show its window, then quit.
            _showEvent?.Set();
            Exit();
            return;
        }

        // If the exe moved (reinstall, new build folder), point the autostart
        // entry at the current exe again so it doesn't launch a stale path.
        if (Services.GetRequiredService<ISettingsService>().Current.StartWithWindows)
            AutoStart.Apply(true);

        Services.GetRequiredService<ISensorService>().Start();
        Services.GetRequiredService<IMetricsHistory>().Start();   // chart data, from app start
        var autoMode = Services.GetRequiredService<IAutoModeService>();
        autoMode.Start();
        Services.GetRequiredService<IQuietModeController>().Start();

        MainWindow = new MainWindow();

        _tray = new TrayIcon(
            iconPath: Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            tooltip: "GameTuner",
            onOpen: ShowMainWindow,
            isAutoModeOn: () => Services.GetRequiredService<ISettingsService>().Current.AutoModeEnabled,
            onToggleAutoMode: ToggleAutoMode,
            onExit: ExitApp);

        autoMode.Notification += (_, message) => _tray?.ShowNotification("GameTuner", message);

        bool startHidden = cmd.Contains(AutoStart.MinimizedArg);
        if (!startHidden) MainWindow.Activate();
    }

    public static void ShowMainWindow() => MainWindow?.ShowAndActivate();

    private static void ToggleAutoMode()
    {
        var settings = Services.GetRequiredService<ISettingsService>();
        settings.Current.AutoModeEnabled = !settings.Current.AutoModeEnabled;
        settings.Save();
    }

    /// <summary>Real exit (from the tray menu or after "restart as admin").</summary>
    public void ExitApp()
    {
        _log?.LogInformation("Exiting");
        _tray?.Dispose();
        _tray = null;

        // Dispose order matters: auto mode restores the power plan, then sensors stop.
        Services.GetRequiredService<IAutoModeService>().Dispose();
        Services.GetRequiredService<IQuietModeController>().Dispose();   // turbo boost back to normal
        Services.GetRequiredService<IFpsMonitor>().Dispose();
        Services.GetRequiredService<ISensorService>().Dispose();
        Services.GetRequiredService<IGameWatcher>().Dispose();

        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        (Services as IDisposable)?.Dispose();   // flushes the Serilog file sink

        MainWindow?.CloseForReal();
        Exit();
    }

    /// <summary>
    /// Single instance via a named mutex.
    /// The first instance owns the mutex and waits on a named event; a second
    /// instance sets that event ("please show your window") and quits.
    ///
    /// After "restart as admin" the new instance starts while the old one is
    /// still shutting down, so it waits up to 10 s for the mutex instead of quitting.
    /// </summary>
    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        _singleInstance = new Mutex(false, MutexName);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        bool owned;
        try
        {
            owned = _singleInstance.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // previous instance crashed without releasing it — it's ours now
        }

        if (!owned)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            return false;
        }

        // Background thread waiting for "show" requests from new instances.
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var showEvent = _showEvent;
        new Thread(() =>
        {
            while (showEvent.WaitOne())
                dispatcher.TryEnqueue(ShowMainWindow);
        }) { IsBackground = true, Name = "SingleInstanceListener" }.Start();

        return true;
    }

    private static IServiceProvider ConfigureServices()
    {
        Directory.CreateDirectory(AppPaths.LogsFolder);
        var serilog = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsFolder, "gametuner-.log"),
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => Serilog.SerilogLoggingBuilderExtensions.AddSerilog(b, serilog, dispose: true));
        services.AddGameTunerCore();

        services.AddSingleton<SystemProfileService>();
        services.AddSingleton<IDialogService, DialogService>();

        // View models are singletons: their state (and live timers) survive
        // navigating between pages.
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<GamesViewModel>();
        services.AddSingleton<OptimizationViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ChartsViewModel>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<SecurityViewModel>();

        return services.BuildServiceProvider();
    }
}
