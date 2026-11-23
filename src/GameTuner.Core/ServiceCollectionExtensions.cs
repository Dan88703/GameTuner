using GameTuner.Core.Automation;
using GameTuner.Core.Backup;
using GameTuner.Core.Games;
using GameTuner.Core.Hardware;
using GameTuner.Core.Monitoring;
using GameTuner.Core.Performance;
using GameTuner.Core.Settings;
using GameTuner.Core.Tuning;
using Microsoft.Extensions.DependencyInjection;

namespace GameTuner.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every Core service. All are singletons: there is one PC,
    /// one sensor loop and one settings file per running app.
    /// </summary>
    public static IServiceCollection AddGameTunerCore(this IServiceCollection services)
    {
        services.AddSingleton<IHardwareScanner, HardwareScanner>();
        services.AddSingleton<ITierCalculator, TierCalculator>();
        services.AddSingleton<IBackupService>(_ => new BackupService());

        // Game profiles: to support a new game, add one line here.
        services.AddSingleton<GameProfile, Cs2Profile>();
        services.AddSingleton<GameProfile, MinecraftProfile>();
        services.AddSingleton<GameProfile, GtaVProfile>();
        services.AddSingleton<IGameLibrary, GameLibrary>();

        services.AddSingleton<IPowerManager, PowerManager>();
        services.AddSingleton<IWindowsGameSettings, WindowsGameSettings>();
        services.AddSingleton<IProcessManager, ProcessManager>();

        services.AddSingleton<ISensorService, SensorService>();
        services.AddSingleton<IFpsMonitor, PresentMonService>();
        services.AddSingleton<IMetricsHistory, MetricsHistory>();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<IGameWatcher, GameWatcher>();
        services.AddSingleton<IAutoModeService, AutoModeService>();
        return services;
    }
}
