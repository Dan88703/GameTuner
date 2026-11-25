namespace GameTuner.Core.Monitoring;

/// <summary>
/// One reading of all sensors. Nullable values = not available on this PC
/// or not available without admin rights.
/// </summary>
public sealed record SensorSnapshot
{
    public DateTime Time { get; init; } = DateTime.Now;

    public double CpuLoad { get; init; }
    public double? CpuTempC { get; init; }

    /// <summary>
    /// True when CpuTempC comes from the ACPI thermal zone (no driver needed) rather
    /// than the CPU's own sensor. Usually close to the real value, but less precise.
    /// </summary>
    public bool CpuTempIsAcpi { get; init; }
    public double? CpuClockMhz { get; init; }

    public double? GpuLoad { get; init; }
    public double? GpuTempC { get; init; }
    public double? GpuClockMhz { get; init; }
    public string? GpuName { get; init; }

    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public int RamPercent { get; init; }

    public bool OnBattery { get; init; }
    /// <summary>0..100, or -1 if there is no battery.</summary>
    public int BatteryPercent { get; init; } = -1;

    /// <summary>True when temperatures/clocks come from LibreHardwareMonitor (admin mode).</summary>
    public bool IsDetailed { get; init; }

    public static SensorSnapshot Empty { get; } = new();
}

public interface ISensorProvider : IDisposable
{
    SensorSnapshot Read();
}
