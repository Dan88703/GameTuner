using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTuner.App.Services;
using GameTuner.Core.Automation;
using GameTuner.Core.Games;
using GameTuner.Core.Hardware;
using GameTuner.Core.Monitoring;
using GameTuner.Core.Tuning;
using Microsoft.UI.Dispatching;

namespace GameTuner.App.ViewModels;

/// <summary>One row in "Последние сессии".</summary>
public sealed record SessionRow(string Game, string When, string Duration, string Fps, string Temps);

/// <summary>
/// "Обзор" page: hardware summary, live sensor tiles, the active game and
/// recent sessions. A 1-second UI timer copies the latest sensor snapshot
/// into bindable properties (the sensor loop itself runs on its own thread).
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly ISensorService _sensors;
    private readonly IFpsMonitor _fps;
    private readonly IAutoModeService _autoMode;
    private readonly ISessionStore _sessions;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;

    public DashboardViewModel(SystemProfileService profile, ISensorService sensors, IFpsMonitor fps,
                              IAutoModeService autoMode, ISessionStore sessions)
    {
        _sensors = sensors;
        _fps = fps;
        _autoMode = autoMode;
        _sessions = sessions;

        // View models are created on the UI thread (first page navigation),
        // so this is the UI dispatcher.
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();

        // These events come from background threads: hop to the UI thread.
        _autoMode.StateChanged += (_, _) => _dispatcher.TryEnqueue(RefreshGame);
        _sessions.Changed += (_, _) => _dispatcher.TryEnqueue(RefreshSessions);

        IsElevated = Elevation.IsElevated;
        RefreshGame();
        RefreshSessions();
        _ = LoadHardwareAsync(profile);
    }

    // ---------- Hardware ----------
    [ObservableProperty] private string _tierText = "Анализ системы…";
    [ObservableProperty] private string _cpuName = "—";
    [ObservableProperty] private string _gpuName = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _diskText = "—";
    [ObservableProperty] private string _screenText = "—";
    [ObservableProperty] private string _scoresText = "";
    [ObservableProperty] private string _deviceText = "—";
    public ObservableCollection<string> Notes { get; } = new();

    // ---------- Live tiles ----------
    [ObservableProperty] private double _cpuLoad;
    [ObservableProperty] private string _cpuLoadText = "—";
    [ObservableProperty] private string _cpuDetailsText = "";
    [ObservableProperty] private double _gpuLoad;
    [ObservableProperty] private string _gpuLoadText = "—";
    [ObservableProperty] private string _gpuDetailsText = "";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramLoadText = "—";
    [ObservableProperty] private string _ramDetailsText = "";
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private bool _isOnBattery;

    // ---------- Game ----------
    [ObservableProperty] private string _gameTitle = "Игра не запущена";
    [ObservableProperty] private string _gameSubtitle = "Авто-режим ждёт запуска игры";
    [ObservableProperty] private string _fpsText = "—";
    [ObservableProperty] private string _fpsLowText = "";
    [ObservableProperty] private bool _isGameRunning;

    // ---------- Misc ----------
    [ObservableProperty] private bool _isElevated;
    public bool ShowAdminHint => !IsElevated;
    public ObservableCollection<SessionRow> Sessions { get; } = new();
    [ObservableProperty] private bool _hasNoSessions = true;

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (Elevation.RestartElevated(App.ElevatedRestartArg))
            ((App)Microsoft.UI.Xaml.Application.Current).ExitApp();
    }

    private async Task LoadHardwareAsync(SystemProfileService profile)
    {
        var (info, tier) = await profile.GetAsync();   // continues on the UI thread

        TierText = $"Рекомендуемый уровень графики: {GenericAdvice.TierName(tier.Tier)}";
        CpuName = $"{info.CpuName} · {info.CpuCores} ядер / {info.CpuThreads} потоков";
        GpuName = string.Join("\n", info.Gpus.Select(g =>
            $"{g.Name} · {(g.VramGb > 0 ? $"{g.VramGb} ГБ" : "? ГБ")} · {(g.IsIntegrated ? "встроенная" : "дискретная")}"));
        RamText = $"{info.RamGb} ГБ";
        DeviceText = info.DeviceKind switch
        {
            DeviceKind.Laptop => "Ноутбук",
            DeviceKind.Desktop => "Компьютер (ПК)",
            _ => "Не определён"
        };
        DiskText = info.SystemDriveIsSsd switch { true => "SSD", false => "HDD", null => "не определён" };
        ScreenText = info.ScreenWidth > 0
            ? $"{info.ScreenWidth}×{info.ScreenHeight}{(info.RefreshRateHz > 0 ? $" · {info.RefreshRateHz} Гц" : "")}"
            : "—";
        ScoresText = $"Оценки: видеокарта {tier.GpuScore}/100 · процессор {tier.CpuScore}/100 · память {tier.RamScore}/100";

        Notes.Clear();
        foreach (string note in tier.Notes) Notes.Add(note);
    }

    private void RefreshLive()
    {
        var s = _sensors.Latest;

        CpuLoad = s.CpuLoad;
        CpuLoadText = $"{s.CpuLoad:0}%";
        CpuDetailsText = JoinParts(
            s.CpuTempC is double ct ? $"{ct:0} °C" : null,
            s.CpuClockMhz is double cc ? $"{cc / 1000:0.0} ГГц" : null,
            IsElevated
                ? (s.CpuTempC == null ? "датчик температуры недоступен" : null)
                : "температура — в режиме админа");

        GpuLoad = s.GpuLoad ?? 0;
        GpuLoadText = s.GpuLoad is double gl ? $"{gl:0}%" : "—";
        GpuDetailsText = JoinParts(
            s.GpuTempC is double gt ? $"{gt:0} °C" : null,
            s.GpuClockMhz is double gc ? $"{gc:0} МГц" : null);

        RamPercent = s.RamPercent;
        RamLoadText = $"{s.RamPercent}%";
        RamDetailsText = $"{s.RamUsedGb} из {s.RamTotalGb} ГБ";

        IsOnBattery = s.OnBattery;
        PowerText = s.BatteryPercent < 0
            ? "От сети"
            : s.OnBattery
                ? $"От батареи · {s.BatteryPercent}% — подключи зарядку!"
                : $"От сети · батарея {s.BatteryPercent}%";

        var live = _fps.Live;
        FpsText = live != null ? $"{live.AverageFps:0}" : "—";
        FpsLowText = live != null ? $"1% low: {live.OnePercentLowFps:0}" : FpsHint();
    }

    private string FpsHint()
    {
        if (!IsGameRunning) return "";
        if (!_fps.IsInstalled) return "Установи PresentMon в настройках";
        if (!IsElevated) return "Нужен режим администратора";
        // Real state from the capture service: running, waiting, or why it stopped.
        return _fps.Status;
    }

    private void RefreshGame()
    {
        var game = _autoMode.ActiveGame;
        IsGameRunning = game != null;
        GameTitle = game?.DisplayName ?? "Игра не запущена";
        GameSubtitle = game != null
            ? $"Процесс {game.ProcessName}.exe · PID {game.ProcessId}"
            : "Авто-режим включится сам, когда ты запустишь игру";
    }

    private void RefreshSessions()
    {
        Sessions.Clear();
        foreach (var r in _sessions.All.Take(10))
        {
            Sessions.Add(new SessionRow(
                r.GameName,
                r.StartedAt.ToString("dd.MM HH:mm"),
                r.Duration.ToString(@"h\:mm"),
                r.AverageFps is double avg ? $"{avg:0} FPS · 1% low {r.OnePercentLowFps:0}" : "FPS не записан",
                JoinParts(
                    r.MaxCpuTempC is double c ? $"CPU до {c:0} °C" : null,
                    r.MaxGpuTempC is double g ? $"GPU до {g:0} °C" : null,
                    $"CPU {r.AverageCpuLoad:0}%",
                    r.AverageGpuLoad is double gl ? $"GPU {gl:0}%" : null)));
        }
        HasNoSessions = Sessions.Count == 0;
    }

    private static string JoinParts(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
