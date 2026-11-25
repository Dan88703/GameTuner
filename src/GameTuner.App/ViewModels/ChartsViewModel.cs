using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameTuner.App.Controls;
using GameTuner.Core.Automation;
using GameTuner.Core.Monitoring;
using GameTuner.Core.Tuning;
using Microsoft.UI.Dispatching;

namespace GameTuner.App.ViewModels;

/// <summary>
/// "Графики" page: load, temperature and FPS charts, either live (last 1–10 min)
/// or for a finished game session.
///
/// Data flow: MetricsHistory (Core) collects one point per second in the
/// background → every second this view model takes a snapshot and turns it
/// into immutable ChartData objects → x:Bind hands them to LineChart, which
/// redraws when it gets a new instance.
/// </summary>
public partial class ChartsViewModel : ObservableObject
{
    private static readonly TimeSpan[] Ranges = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10) };

    private readonly IMetricsHistory _history;
    private readonly ISensorService _sensors;
    private readonly ISessionStore _sessions;
    private readonly DispatcherQueue _dispatcher;
    // Kept in a field so the timer can't be garbage-collected while the app runs.
    private readonly DispatcherQueueTimer _timer;
    private List<GameSessionRecord> _sessionList = new();

    public ChartsViewModel(IMetricsHistory history, ISessionStore sessions, ISensorService sensors)
    {
        _history = history;
        _sensors = sensors;
        _sessions = sessions;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        RangeOptions = new ObservableCollection<string> { "1 минута", "5 минут", "10 минут" };
        RefreshSessionList();
        _sessions.Changed += (_, _) => _dispatcher.TryEnqueue(RefreshSessionList);

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => { if (IsLive) BuildLive(); };
        _timer.Start();

        BuildLive();
    }

    public ObservableCollection<string> RangeOptions { get; }
    public ObservableCollection<string> SourceOptions { get; } = new();

    [ObservableProperty] private int _selectedRangeIndex = 1;   // 5 minutes
    [ObservableProperty] private int _selectedSourceIndex;      // 0 = live
    [ObservableProperty] private bool _isLive = true;

    [ObservableProperty] private ChartData? _loadChart;
    [ObservableProperty] private ChartData? _tempChart;
    [ObservableProperty] private ChartData? _fpsChart;

    [ObservableProperty] private string _loadSummary = "";
    [ObservableProperty] private string _tempSummary = "";
    [ObservableProperty] private string _fpsSummary = "";

    partial void OnSelectedRangeIndexChanged(int value)
    {
        if (IsLive) BuildLive();
    }

    partial void OnSelectedSourceIndexChanged(int value)
    {
        IsLive = value <= 0;
        if (IsLive) BuildLive();
        else if (value - 1 < _sessionList.Count) BuildSession(_sessionList[value - 1]);
    }

    // ------------------------------------------------------------------

    private void BuildLive()
    {
        var window = Ranges[Math.Clamp(SelectedRangeIndex, 0, Ranges.Length - 1)];
        var points = _history.Snapshot(window);
        DateTime end = points.Count > 0 ? points[^1].Time : DateTime.Now;
        DateTime start = end - window;

        string left = $"−{window.TotalMinutes:0} мин";
        Build(points, start, end, left, "сейчас");
    }

    private void BuildSession(GameSessionRecord session)
    {
        var points = session.Timeline;
        DateTime start = points.Count > 0 ? points[0].Time : session.StartedAt;
        DateTime end = points.Count > 1 ? points[^1].Time : start + session.Duration;
        Build(points, start, end, start.ToString("HH:mm"), end.ToString("HH:mm"));
    }

    /// <summary>Turns metric points into the three charts and their text summaries.</summary>
    private void Build(IReadOnlyList<MetricPoint> points, DateTime start, DateTime end, string leftLabel, string rightLabel)
    {
        double span = Math.Max((end - start).TotalSeconds, 1);
        // X position of a point: 0 at the left edge (start), 1 at the right edge (end).
        double X(MetricPoint p) => (p.Time - start).TotalSeconds / span;

        ChartSeries Series(string name, Windows.UI.Color color, Func<MetricPoint, double?> value) =>
            new(name, color, points.Select(p => new ChartPoint(X(p), value(p))).ToList());

        // --- Load: fixed 0–100 % scale ---
        LoadChart = new ChartData(
            new[]
            {
                Series("CPU", LineChart.Palette.Cpu, p => p.CpuLoad),
                Series("GPU", LineChart.Palette.Gpu, p => p.GpuLoad),
                Series("RAM", LineChart.Palette.Ram, p => p.RamPercent)
            },
            0, 100, "%", leftLabel, rightLabel);
        LoadSummary = Summary(points, p => p.CpuLoad, "CPU", "%") + "   " + Summary(points, p => p.GpuLoad, "GPU", "%");

        // --- Temperature: scale grows to fit the data, at least 30–90 °C ---
        var temps = points.SelectMany(p => new[] { p.CpuTempC, p.GpuTempC }).OfType<double>().ToList();
        double tMin = Math.Min(30, temps.Count > 0 ? Math.Floor(temps.Min() / 10) * 10 : 30);
        double tMax = Math.Max(90, temps.Count > 0 ? Math.Ceiling(temps.Max() / 10) * 10 : 90);
        TempChart = new ChartData(
            new[]
            {
                Series(_sensors.Latest.CpuTempIsAcpi ? "CPU (ACPI)" : "CPU", LineChart.Palette.Cpu, p => p.CpuTempC),
                Series("GPU", LineChart.Palette.Gpu, p => p.GpuTempC)
            },
            tMin, tMax, "°", leftLabel, rightLabel,
            _sensors.TemperatureHint ?? "Нет данных");
        TempSummary = Summary(points, p => p.CpuTempC, "CPU", " °C") + "   " + Summary(points, p => p.GpuTempC, "GPU", " °C")
                      + TemperatureWarning(temps)
                      // Explain why something is missing even when part of the data is there.
                      + (_sensors.TemperatureHint is string hint && temps.Count > 0 ? "\n" + hint : "");

        // --- FPS: scale rounded up to a multiple of 30, at least 0–60 ---
        var fps = points.Select(p => p.Fps).OfType<double>().ToList();
        double fpsMax = Math.Max(60, fps.Count > 0 ? Math.Ceiling(fps.Max() * 1.1 / 30) * 30 : 60);
        FpsChart = new ChartData(
            new[] { Series("FPS", LineChart.Palette.Fps, p => p.Fps) },
            0, fpsMax, "", leftLabel, rightLabel,
            "FPS записывается во время игры (нужны PresentMon и режим администратора)");
        FpsSummary = Summary(points, p => p.Fps, "FPS", "", showMin: true);
    }

    /// <summary>"CPU: сейчас 24% · средн. 31% · макс. 87%" for one metric.</summary>
    private static string Summary(IReadOnlyList<MetricPoint> points, Func<MetricPoint, double?> value,
                                  string name, string unit, bool showMin = false)
    {
        var values = points.Select(value).OfType<double>().ToList();
        if (values.Count == 0) return $"{name}: нет данных";

        string text = $"{name}: сейчас {values[^1]:0}{unit} · средн. {values.Average():0}{unit}";
        text += showMin ? $" · мин. {values.Min():0}{unit}" : $" · макс. {values.Max():0}{unit}";
        return text;
    }

    /// <summary>
    /// Laptop chips start throttling (lowering clocks to cool down) at roughly
    /// 90–95 °C, which shows up in games as sudden FPS drops.
    /// </summary>
    private static string TemperatureWarning(List<double> temps)
    {
        if (temps.Count == 0) return "";
        double max = temps.Max();
        return max >= 90 ? "\n⚠ Выше 90 °C — ноутбук, скорее всего, троттлит. Подставка с охлаждением и чистка от пыли помогут."
             : max >= 80 ? "\nГорячо, но в пределах нормы для игрового ноутбука."
             : "";
    }

    private void RefreshSessionList()
    {
        int previous = SelectedSourceIndex;
        _sessionList = _sessions.All.Where(s => s.Timeline.Count > 0).Take(20).ToList();

        SourceOptions.Clear();
        SourceOptions.Add("Сейчас (в реальном времени)");
        foreach (var s in _sessionList)
            SourceOptions.Add($"{s.GameName} · {s.StartedAt:dd.MM HH:mm} · {s.Duration:h\\:mm}");

        // Refilling the list resets the ComboBox selection: put it back.
        SelectedSourceIndex = previous < SourceOptions.Count ? Math.Max(previous, 0) : 0;
    }
}
