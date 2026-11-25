using System.Diagnostics;
using System.Runtime.InteropServices;
using GameTuner.Core.Interop;

namespace GameTuner.Core.Monitoring;

/// <summary>
/// Sensors that work WITHOUT admin rights: CPU load, RAM, battery and GPU load.
/// No temperatures — Windows does not expose them to normal users.
/// </summary>
public sealed class BasicSensorProvider : ISensorProvider
{
    private readonly GpuEngineCounter _gpu = new();
    private readonly ThermalZoneCounter _thermal = new();
    private ulong _prevIdle, _prevKernel, _prevUser;

    public SensorSnapshot Read()
    {
        double ramUsed = ReadMemory(out double ramTotal, out int ramPercent);
        bool onBattery = ReadPower(out int battery);

        double? acpiTemp = _thermal.Read();
        return new SensorSnapshot
        {
            CpuLoad = ReadCpuLoad(),
            CpuTempC = acpiTemp,
            CpuTempIsAcpi = acpiTemp.HasValue,
            GpuLoad = _gpu.Read(),
            RamUsedGb = ramUsed,
            RamTotalGb = ramTotal,
            RamPercent = ramPercent,
            OnBattery = onBattery,
            BatteryPercent = battery
        };
    }

    /// <summary>
    /// Windows only gives TOTAL idle/kernel/user CPU time since boot. "Load now"
    /// is the difference between two samples:
    ///   busy  = (kernelΔ + userΔ) − idleΔ     (kernel time includes idle time)
    ///   load  = busy / (kernelΔ + userΔ)
    /// The first call has nothing to compare with and returns 0.
    /// </summary>
    private double ReadCpuLoad()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        ulong idleDelta = idle.Value - _prevIdle;
        ulong total = (kernel.Value - _prevKernel) + (user.Value - _prevUser);
        bool first = _prevKernel == 0;

        _prevIdle = idle.Value;
        _prevKernel = kernel.Value;
        _prevUser = user.Value;

        if (first || total == 0 || idleDelta > total) return 0;
        return Math.Round((total - idleDelta) * 100.0 / total, 1);
    }

    private static double ReadMemory(out double totalGb, out int percent)
    {
        var mem = new Native.MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref mem))
        {
            totalGb = 0;
            percent = 0;
            return 0;
        }
        totalGb = Math.Round(mem.TotalPhys / 1073741824.0, 1);
        percent = (int)mem.MemoryLoad;
        return Math.Round((mem.TotalPhys - mem.AvailPhys) / 1073741824.0, 1);
    }

    private static bool ReadPower(out int batteryPercent)
    {
        batteryPercent = -1;
        if (!Native.GetSystemPowerStatus(out var status)) return false;
        // BatteryFlag 128 = "no system battery" (desktop PC).
        if (status.BatteryFlag != 128 && status.BatteryLifePercent <= 100)
            batteryPercent = status.BatteryLifePercent;
        return status.ACLineStatus == 0;
    }

    public void Dispose()
    {
        _gpu.Dispose();
        _thermal.Dispose();
    }
}

/// <summary>
/// GPU load from the "GPU Engine" performance counters — the same source Task
/// Manager uses, available to normal users.
///
/// Windows creates one counter instance per (process × GPU × engine), named like
///   "pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D".
/// We take only 3D engines, sum them per GPU (the luid part identifies the GPU),
/// and report the busiest GPU — on a laptop that is the one running the game.
/// Instances appear and disappear with processes, so the list is refreshed every 5 s.
/// </summary>
internal sealed class GpuEngineCounter : IDisposable
{
    private const string Category = "GPU Engine";
    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _unavailable;

    public double? Read()
    {
        if (_unavailable) return null;
        try
        {
            if (DateTime.UtcNow - _lastRefresh > TimeSpan.FromSeconds(5)) RefreshInstances();

            var perGpu = new Dictionary<string, double>();
            foreach (var (instance, counter) in _counters)
            {
                float value;
                try { value = counter.NextValue(); }
                catch (InvalidOperationException) { continue; } // process exited since refresh

                string luid = ExtractLuid(instance);
                perGpu[luid] = perGpu.GetValueOrDefault(luid) + value;
            }
            return perGpu.Count == 0 ? 0 : Math.Round(Math.Min(100, perGpu.Values.Max()), 1);
        }
        catch (Exception)
        {
            // Counters can be disabled or corrupted on some systems ("lodctr /r" fixes it).
            _unavailable = true;
            return null;
        }
    }

    private void RefreshInstances()
    {
        _lastRefresh = DateTime.UtcNow;
        var names = new PerformanceCounterCategory(Category).GetInstanceNames()
            .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
            .ToHashSet();

        foreach (string gone in _counters.Keys.Where(k => !names.Contains(k)).ToList())
        {
            _counters[gone].Dispose();
            _counters.Remove(gone);
        }
        foreach (string name in names.Where(n => !_counters.ContainsKey(n)))
        {
            var counter = new PerformanceCounter(Category, "Utilization Percentage", name, readOnly: true);
            counter.NextValue(); // the first sample is always 0, prime it
            _counters[name] = counter;
        }
    }

    internal static string ExtractLuid(string instance)
    {
        int start = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        int end = instance.IndexOf("_phys", StringComparison.OrdinalIgnoreCase);
        return start >= 0 && end > start ? instance[start..end] : "unknown";
    }

    public void Dispose()
    {
        foreach (var c in _counters.Values) c.Dispose();
        _counters.Clear();
    }
}

/// <summary>
/// Fallback temperature from the ACPI "thermal zones" — the sensors the laptop's
/// firmware uses to decide when to spin the fans. Windows exposes them as the
/// "Thermal Zone Information" performance counters, readable WITHOUT admin
/// rights or extra drivers.
///
/// They are not the CPU's own core sensor, but on most laptops the hottest zone
/// sits right next to the CPU and follows it closely. Some PCs report a fake
/// constant (often 27.8 °C) or nothing — those values are filtered out.
/// </summary>
internal sealed class ThermalZoneCounter : IDisposable
{
    private const string Category = "Thermal Zone Information";
    private List<PerformanceCounter>? _counters;
    private bool _unavailable;
    private readonly Queue<double> _recent = new();

    public double? Read()
    {
        if (_unavailable) return null;
        try
        {
            _counters ??= CreateCounters();
            if (_counters.Count == 0) { _unavailable = true; return null; }

            double max = _counters.Select(ReadCelsius).Max();
            if (!IsPlausible(max)) return null;

            // A value that never changes for minutes is a firmware placeholder, not a sensor.
            _recent.Enqueue(max);
            if (_recent.Count > 120) _recent.Dequeue();
            if (_recent.Count == 120 && _recent.All(v => v == max)) return null;

            return Math.Round(max, 1);
        }
        catch (Exception)
        {
            _unavailable = true;
            return null;
        }
    }

    internal static bool IsPlausible(double celsius) => celsius is > 20 and < 115;

    private static List<PerformanceCounter> CreateCounters()
    {
        var category = new PerformanceCounterCategory(Category);
        // "High Precision Temperature" (tenths of a kelvin) exists on Windows 10 1903+;
        // older systems only have "Temperature" (whole kelvins).
        bool highPrecision = category.CounterExists("High Precision Temperature");
        string counterName = highPrecision ? "High Precision Temperature" : "Temperature";

        return category.GetInstanceNames()
            .Select(instance => new PerformanceCounter(Category, counterName, instance, readOnly: true))
            .ToList();
    }

    /// <summary>Kelvin (or tenths of a kelvin) → °C.</summary>
    private static double ReadCelsius(PerformanceCounter counter)
    {
        double raw = counter.NextValue();
        double kelvin = counter.CounterName.StartsWith("High", StringComparison.Ordinal) ? raw / 10 : raw;
        return kelvin - 273.15;
    }

    public void Dispose()
    {
        if (_counters == null) return;
        foreach (var c in _counters) c.Dispose();
    }
}
