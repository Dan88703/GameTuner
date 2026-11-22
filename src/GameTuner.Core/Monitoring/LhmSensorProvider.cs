using LibreHardwareMonitor.Hardware;

namespace GameTuner.Core.Monitoring;

/// <summary>
/// Temperatures, clocks and loads through LibreHardwareMonitor. Requires admin
/// rights: reading CPU sensors needs a kernel driver that LHM loads itself.
///
/// Note: this class is NOT thread-safe (LHM's Computer isn't either), so it is
/// only ever called from the single polling loop in <see cref="SensorService"/>.
/// </summary>
public sealed class LhmSensorProvider : ISensorProvider
{
    private readonly Computer _computer;

    public LhmSensorProvider()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };
        _computer.Open(); // may throw if the driver is blocked — caller falls back
    }

    public SensorSnapshot Read()
    {
        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }

        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

        // Prefer the discrete GPU: on laptops it's the one running games.
        var gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                  ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);

        return new SensorSnapshot
        {
            CpuLoad = Value(cpu, SensorType.Load, "CPU Total") ?? 0,
            CpuTempC = CpuTemperature(cpu),
            CpuClockMhz = MaxValue(cpu, SensorType.Clock, "Core #"),
            GpuName = gpu?.Name,
            GpuLoad = Value(gpu, SensorType.Load, "GPU Core"),
            GpuTempC = Value(gpu, SensorType.Temperature, "GPU Core") ?? Value(gpu, SensorType.Temperature, null),
            GpuClockMhz = Value(gpu, SensorType.Clock, "GPU Core"),
            IsDetailed = true
        };
    }

    /// <summary>
    /// CPU temperature sensor names differ by vendor:
    /// Intel = "CPU Package", AMD = "Core (Tctl/Tdie)". Fall back to the hottest core.
    /// </summary>
    private static double? CpuTemperature(IHardware? cpu) =>
        Value(cpu, SensorType.Temperature, "CPU Package")
        ?? Value(cpu, SensorType.Temperature, "Core (Tctl/Tdie)")
        ?? Value(cpu, SensorType.Temperature, "Core Max")
        ?? MaxValue(cpu, SensorType.Temperature, "Core");

    /// <summary>First sensor of the type whose name starts with <paramref name="name"/> (null = any).</summary>
    private static double? Value(IHardware? hw, SensorType type, string? name)
    {
        // Value > 0: when LHM's kernel driver could not load (blocked by Windows
        // security or missing), CPU sensors still exist but always report 0.
        // A real temperature or clock is never 0, so 0 means "no data".
        var sensor = AllSensors(hw).FirstOrDefault(s =>
            s.SensorType == type && s.Value is > 0 &&
            (name == null || s.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
        return sensor?.Value is float v ? Math.Round(v, 1) : null;
    }

    private static double? MaxValue(IHardware? hw, SensorType type, string namePrefix)
    {
        var values = AllSensors(hw)
            .Where(s => s.SensorType == type && s.Value is > 0 &&
                        s.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(s => (double)s.Value!.Value)
            .ToList();
        return values.Count > 0 ? Math.Round(values.Max(), 1) : null;
    }

    private static IEnumerable<ISensor> AllSensors(IHardware? hw) =>
        hw == null ? Enumerable.Empty<ISensor>() : hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors));

    public void Dispose() => _computer.Close();
}
