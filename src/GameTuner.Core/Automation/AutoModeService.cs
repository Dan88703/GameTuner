using GameTuner.Core.Monitoring;
using GameTuner.Core.Settings;
using GameTuner.Core.Tuning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Automation;

public interface IAutoModeService : IDisposable
{
    /// <summary>The game currently being played, or null.</summary>
    DetectedGame? ActiveGame { get; }

    /// <summary>Short messages for tray notifications ("CS2 detected, boost on").</summary>
    event EventHandler<string>? Notification;

    /// <summary>Raised when a game starts or stops (for the UI to refresh).</summary>
    event EventHandler? StateChanged;

    void Start();
}

/// <summary>
/// The "it just works" part of GameTuner.
///
/// Game starts  → boost power, Game Mode on, raise game priority, close the
///                user's chosen apps, start FPS capture, start recording sensors.
/// Game stops   → restore power, stop FPS capture, save a session summary.
///
/// Everything is driven by events from <see cref="IGameWatcher"/>, and each
/// step is optional through <see cref="AppSettings"/>. Each step is wrapped
/// in its own try/catch: one failing tweak must not block the others.
/// </summary>
public sealed class AutoModeService : IAutoModeService
{
    private readonly IGameWatcher _watcher;
    private readonly ISettingsService _settings;
    private readonly IPowerManager _power;
    private readonly IWindowsGameSettings _windows;
    private readonly IProcessManager _processes;
    private readonly IFpsMonitor _fps;
    private readonly ISensorService _sensors;
    private readonly ISessionStore _sessions;
    private readonly IQuietModeController _quiet;
    private readonly ILogger<AutoModeService> _log;

    private readonly object _lock = new();
    private SessionAccumulator? _session;
    private bool _weBoostedPower;

    public AutoModeService(IGameWatcher watcher, ISettingsService settings, IPowerManager power,
        IWindowsGameSettings windows, IProcessManager processes, IFpsMonitor fps,
        ISensorService sensors, ISessionStore sessions, IQuietModeController quiet,
        ILogger<AutoModeService>? log = null)
    {
        _quiet = quiet;
        _watcher = watcher;
        _settings = settings;
        _power = power;
        _windows = windows;
        _processes = processes;
        _fps = fps;
        _sensors = sensors;
        _sessions = sessions;
        _log = log ?? NullLogger<AutoModeService>.Instance;
    }

    public DetectedGame? ActiveGame => _watcher.Current;
    public event EventHandler<string>? Notification;
    public event EventHandler? StateChanged;

    public void Start()
    {
        _watcher.GameStarted += OnGameStarted;
        _watcher.GameStopped += OnGameStopped;
        _sensors.Updated += OnSensorsUpdated;
        _watcher.Start();
    }

    private void OnGameStarted(object? sender, DetectedGame game)
    {
        // Quiet mode must be switched off FIRST, so the power boost below
        // saves the user's real settings, not the quiet-mode ones.
        Step("quiet mode off", _quiet.Reevaluate);

        var s = _settings.Current;
        lock (_lock) _session = new SessionAccumulator(game.DisplayName);

        if (!s.AutoModeEnabled)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var done = new List<string>();

        Step("power boost", () =>
        {
            if (!s.BoostPowerOnGameStart) return;
            // Do not touch (or later restore) a boost the user turned on by hand.
            if (_power.IsBoosted) return;
            _power.EnableMaxPerformance();
            _weBoostedPower = true;
            done.Add("макс. питание");
        });

        Step("game mode", () =>
        {
            if (_windows.IsGameModeEnabled()) return;
            _windows.SetGameMode(true);
            done.Add("Game Mode");
        });

        Step("priority", () =>
        {
            if (s.RaiseGamePriority && _processes.SetHighPriority(game.ProcessId))
                done.Add("высокий приоритет");
        });

        Step("close apps", () =>
        {
            if (!s.CloseAppsOnGameStart) return;
            int closed = s.AppsToClose.Sum(name => _processes.CloseByName(name));
            if (closed > 0) done.Add($"закрыто программ: {closed}");
        });

        Step("fps capture", () =>
        {
            if (s.CaptureFps && _fps.CanCapture)
            {
                _fps.Start(game.ProcessId);
                done.Add("счётчик FPS");
            }
        });

        Notify(done.Count > 0
            ? $"{game.DisplayName}: игровой режим включён ({string.Join(", ", done)})"
            : $"{game.DisplayName}: игра обнаружена");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGameStopped(object? sender, DetectedGame game)
    {
        FrameStats? fps = null;
        Step("fps stop", () => fps = _fps.Stop());

        if (_weBoostedPower)
        {
            Step("power restore", () => _power.Restore());
            _weBoostedPower = false;
        }

        // Back to quiet mode only after the original power plan is restored.
        Step("quiet mode on", _quiet.Reevaluate);

        SessionAccumulator? session;
        lock (_lock)
        {
            session = _session;
            _session = null;
        }

        // Sessions shorter than a minute are launches/crashes, not worth saving.
        if (session != null && session.Elapsed >= TimeSpan.FromMinutes(1))
        {
            var record = session.ToRecord(fps);
            _sessions.Add(record);
            Notify(record.AverageFps is double avg
                ? $"{game.DisplayName}: сессия {record.Duration:h\\:mm}, средний FPS {avg:0}, 1% low {record.OnePercentLowFps:0}"
                : $"{game.DisplayName}: сессия {record.Duration:h\\:mm} завершена, питание восстановлено");
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSensorsUpdated(object? sender, SensorSnapshot snapshot)
    {
        // Read FPS outside the lock: it takes PresentMon's own lock.
        double? fps = _fps.Live?.AverageFps;
        lock (_lock) _session?.Add(snapshot, fps);
    }

    private void Step(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { _log.LogWarning(ex, "Auto mode step '{Step}' failed", name); }
    }

    private void Notify(string message)
    {
        _log.LogInformation("{Message}", message);
        if (_settings.Current.ShowNotifications) Notification?.Invoke(this, message);
    }

    public void Dispose()
    {
        _watcher.GameStarted -= OnGameStarted;
        _watcher.GameStopped -= OnGameStopped;
        _sensors.Updated -= OnSensorsUpdated;
        _fps.Stop();
        if (_weBoostedPower) _power.Restore();
        _weBoostedPower = false; // Dispose may be called twice (explicitly and by the DI container)
    }

    /// <summary>Collects sensor peaks and averages while a game is running.</summary>
    internal sealed class SessionAccumulator
    {
        private readonly string _game;
        private readonly DateTime _start = DateTime.Now;
        private double? _maxCpuTemp, _maxGpuTemp;
        private double _cpuLoadSum, _gpuLoadSum;
        private int _samples, _gpuSamples;
        private readonly TimelineBuilder _timeline = new();

        public SessionAccumulator(string game) => _game = game;

        public TimeSpan Elapsed => DateTime.Now - _start;

        public void Add(SensorSnapshot s, double? fps = null)
        {
            _timeline.Add(MetricPoint.From(s, fps));
            _samples++;
            _cpuLoadSum += s.CpuLoad;
            if (s.GpuLoad is double g) { _gpuLoadSum += g; _gpuSamples++; }
            if (s.CpuTempC is double ct) _maxCpuTemp = Math.Max(_maxCpuTemp ?? ct, ct);
            if (s.GpuTempC is double gt) _maxGpuTemp = Math.Max(_maxGpuTemp ?? gt, gt);
        }

        public GameSessionRecord ToRecord(FrameStats? fps) => new()
        {
            GameName = _game,
            StartedAt = _start,
            Duration = Elapsed,
            AverageFps = fps?.AverageFps,
            OnePercentLowFps = fps?.OnePercentLowFps,
            MaxCpuTempC = _maxCpuTemp,
            MaxGpuTempC = _maxGpuTemp,
            AverageCpuLoad = _samples > 0 ? Math.Round(_cpuLoadSum / _samples, 1) : 0,
            AverageGpuLoad = _gpuSamples > 0 ? Math.Round(_gpuLoadSum / _gpuSamples, 1) : null,
            Timeline = _timeline.Build()
        };
    }
}
