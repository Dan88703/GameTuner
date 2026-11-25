namespace GameTuner.Core.Hardware;

/// <summary>Snapshot of the PC hardware, collected once at startup.</summary>
public sealed class SystemInfo
{
    public string CpuName { get; set; } = "Unknown";
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }
    public int CpuMaxClockMhz { get; set; }

    public List<GpuInfo> Gpus { get; set; } = new();

    public double RamGb { get; set; }

    /// <summary>True = SSD, false = HDD, null = could not be detected.</summary>
    public bool? SystemDriveIsSsd { get; set; }

    public bool HasBattery { get; set; }

    /// <summary>Laptop or desktop PC — decides which hints and options make sense.</summary>
    public DeviceKind DeviceKind { get; set; } = DeviceKind.Unknown;

    public bool IsLaptop => DeviceKind == DeviceKind.Laptop;

    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public int RefreshRateHz { get; set; }

    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;

    /// <summary>The GPU that games will most likely use (discrete if there is one).</summary>
    public GpuInfo? PrimaryGpu =>
        Gpus.FirstOrDefault(g => !g.IsIntegrated) ?? Gpus.FirstOrDefault();
}

public enum DeviceKind
{
    Unknown,
    Laptop,
    Desktop
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "Unknown";
    public double VramGb { get; set; }
    public bool IsIntegrated { get; set; }
    public string DriverVersion { get; set; } = "";

    /// <summary>Release date of the installed driver (null if unknown).</summary>
    public DateTime? DriverDate { get; set; }
}
