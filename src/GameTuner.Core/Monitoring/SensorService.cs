using GameTuner.Core.Tuning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Monitoring;

public interface ISensorService : IDisposable
{
    /// <summary>The most recent reading (never null).</summary>
    SensorSnapshot Latest { get; }

    /// <summary>True when temperatures are available (running as admin and LHM loaded).</summary>
    bool HasDetailedSensors { get; }

    /// <summary>
    /// Why precise temperatures are missing, as an instruction for the user;
    /// null when everything works.
    /// </summary>
    string? TemperatureHint { get; }

    /// <summary>Raised on a background thread after every reading.</summary>
    event EventHandler<SensorSnapshot>? Updated;

    void Start();
}

/// <summary>
/// The single owner of all sensor providers. One background loop reads the
/// sensors once per second and publishes the result; everyone else (UI,
/// auto mode, session recorder) only reads <see cref="Latest"/>.
///
/// Why one loop: LibreHardwareMonitor is not thread-safe, and reading sensors
/// from several places at once would also waste CPU.
///
/// Merge rule: basic provider always runs (RAM, battery, CPU load, GPU load);
/// in admin mode LHM adds temperatures and clocks and, when available, its
/// more accurate loads.
/// </summary>
public sealed class SensorService : ISensorService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ILogger<SensorService> _log;
    private readonly BasicSensorProvider _basic = new();
    private LhmSensorProvider? _lhm;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SensorService(ILogger<SensorService>? log = null)
    {
        _log = log ?? NullLogger<SensorService>.Instance;
    }

    public SensorSnapshot Latest { get; private set; } = SensorSnapshot.Empty;
    public bool HasDetailedSensors => _lhm != null;

    public string? TemperatureHint =>
        !Elevation.IsElevated
            ? "Точные температуры доступны в режиме администратора."
            : !LhmSensorProvider.IsPawnIoInstalled()
                ? "Для точной температуры процессора нужен бесплатный драйвер PawnIO: Настройки → «Мониторинг и FPS» → «Скачать PawnIO», потом перезапусти GameTuner."
                : Latest.CpuTempC == null && Latest.GpuTempC == null
                    ? "Датчики температуры этого устройства пока не поддерживаются."
                    : null;
    public event EventHandler<SensorSnapshot>? Updated;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // LongRunning = a dedicated thread instead of a thread-pool thread,
        // because this loop lives for the whole app lifetime.
        _loop = Task.Factory.StartNew(() => Loop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Loop(CancellationToken token)
    {
        TryStartLhm();

        while (!token.IsCancellationRequested)
        {
            try
            {
                Latest = Merge(_basic.Read(), ReadLhmSafe());
                Updated?.Invoke(this, Latest);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sensor read failed");
            }
            token.WaitHandle.WaitOne(Interval);
        }
    }

    private void TryStartLhm()
    {
        if (!Elevation.IsElevated) return;
        try
        {
            _lhm = new LhmSensorProvider();
            _log.LogInformation("LibreHardwareMonitor started, detailed sensors enabled");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LibreHardwareMonitor failed to start; temperatures unavailable");
            _lhm = null;
        }
    }

    private SensorSnapshot? ReadLhmSafe()
    {
        if (_lhm == null) return null;
        try { return _lhm.Read(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LibreHardwareMonitor read failed; disabling it");
            _lhm.Dispose();
            _lhm = null;
            return null;
        }
    }

    internal static SensorSnapshot Merge(SensorSnapshot basic, SensorSnapshot? detailed)
    {
        if (detailed == null) return basic;
        return basic with
        {
            CpuLoad = detailed.CpuLoad > 0 ? detailed.CpuLoad : basic.CpuLoad,
            // Precise CPU sensor if LHM has it, otherwise keep the ACPI fallback.
            CpuTempC = detailed.CpuTempC ?? basic.CpuTempC,
            CpuTempIsAcpi = detailed.CpuTempC == null && basic.CpuTempIsAcpi,
            CpuClockMhz = detailed.CpuClockMhz,
            GpuLoad = detailed.GpuLoad ?? basic.GpuLoad,
            GpuTempC = detailed.GpuTempC,
            GpuClockMhz = detailed.GpuClockMhz,
            GpuName = detailed.GpuName,
            IsDetailed = true
        };
    }

    public void Dispose()
    {
        _cts?.Cancel();
        // Wait for the loop to finish its current read before disposing the
        // providers it is using.
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _basic.Dispose();
        _lhm?.Dispose();
    }
}
