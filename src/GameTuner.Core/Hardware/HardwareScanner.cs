using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace GameTuner.Core.Hardware;

public interface IHardwareScanner
{
    SystemInfo Scan();
}

/// <summary>
/// Reads hardware information from Windows through WMI and the registry.
/// Every query is wrapped in try/catch: on some laptops a WMI class can be
/// missing or broken, and one failed query must not break the whole scan.
/// </summary>
public sealed class HardwareScanner : IHardwareScanner
{
    private readonly ILogger<HardwareScanner> _log;

    public HardwareScanner(ILogger<HardwareScanner>? log = null)
    {
        _log = log ?? NullLogger<HardwareScanner>.Instance;
    }

    // Registry class key that holds one subkey per display adapter driver.
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public SystemInfo Scan()
    {
        var info = new SystemInfo();
        ReadCpu(info);
        ReadGpus(info);
        ReadRam(info);
        ReadSystemDrive(info);
        ReadBattery(info);
        info.DeviceKind = DetectDeviceKind(ReadChassisTypes(), info.HasBattery);

        _log.LogInformation("Hardware scan: {Kind}, CPU={Cpu}, GPUs={Gpus}, RAM={Ram} GB, SSD={Ssd}",
            info.DeviceKind, info.CpuName, string.Join(" | ", info.Gpus.Select(g => $"{g.Name} {g.VramGb} GB")),
            info.RamGb, info.SystemDriveIsSsd);
        return info;
    }

    private void ReadCpu(SystemInfo info)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject cpu in searcher.Get())
            {
                info.CpuName = (cpu["Name"] as string ?? "Unknown").Trim();
                info.CpuCores = Convert.ToInt32(cpu["NumberOfCores"] ?? 0);
                info.CpuThreads = Convert.ToInt32(cpu["NumberOfLogicalProcessors"] ?? 0);
                info.CpuMaxClockMhz = Convert.ToInt32(cpu["MaxClockSpeed"] ?? 0);
                break; // laptops have a single CPU socket
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "CPU query failed"); }

        if (info.CpuThreads == 0) info.CpuThreads = Environment.ProcessorCount;
    }

    /// <summary>
    /// Reads all video adapters.
    /// Why the registry: Win32_VideoController.AdapterRAM is a 32-bit value,
    /// so any GPU with more than 4 GB of VRAM is reported as 4 GB (or less).
    /// The driver writes the real 64-bit size into
    /// "HardwareInformation.qwMemorySize" under the display class key, so we
    /// prefer that value and fall back to AdapterRAM only if it is missing.
    /// </summary>
    private void ReadGpus(SystemInfo info)
    {
        var registryVram = ReadVramFromRegistry();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
            foreach (ManagementObject gpu in searcher.Get())
            {
                string name = (gpu["Name"] as string ?? "Unknown").Trim();

                // Skip virtual adapters (remote desktop tools, streaming apps, etc.)
                if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                    continue;

                double vramGb;
                if (registryVram.TryGetValue(name, out long bytes) && bytes > 0)
                    vramGb = bytes / 1024.0 / 1024 / 1024;
                else
                    vramGb = Convert.ToUInt32(gpu["AdapterRAM"] ?? 0u) / 1024.0 / 1024 / 1024;

                info.Gpus.Add(new GpuInfo
                {
                    Name = name,
                    VramGb = Math.Round(vramGb, 1),
                    DriverVersion = gpu["DriverVersion"] as string ?? "",
                    DriverDate = ParseWmiDate(gpu["DriverDate"] as string),
                    IsIntegrated = IsIntegratedGpu(name)
                });

                // The adapter driving the screen reports the current resolution.
                int w = Convert.ToInt32(gpu["CurrentHorizontalResolution"] ?? 0);
                int h = Convert.ToInt32(gpu["CurrentVerticalResolution"] ?? 0);
                if (w > info.ScreenWidth)
                {
                    info.ScreenWidth = w;
                    info.ScreenHeight = h;
                    info.RefreshRateHz = Convert.ToInt32(gpu["CurrentRefreshRate"] ?? 0);
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "GPU query failed"); }
    }

    /// <summary>WMI dates look like "20250312000000.000000-000" (DMTF format).</summary>
    internal static DateTime? ParseWmiDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(value); }
        catch (Exception) { return null; }
    }

    /// <summary>Returns DriverDesc -> VRAM bytes for every adapter in the registry.</summary>
    private static Dictionary<string, long> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey == null) return result;

            foreach (string subName in classKey.GetSubKeyNames())
            {
                // Subkeys are "0000", "0001", ... ; "Properties" is not an adapter.
                if (!int.TryParse(subName, out _)) continue;
                try
                {
                    using var adapterKey = classKey.OpenSubKey(subName);
                    if (adapterKey?.GetValue("DriverDesc") is not string desc) continue;

                    object? raw = adapterKey.GetValue("HardwareInformation.qwMemorySize")
                                  ?? adapterKey.GetValue("HardwareInformation.MemorySize");
                    long bytes = raw switch
                    {
                        long l => l,
                        int i => (uint)i,           // DWORD is stored signed, real value is unsigned
                        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0
                    };
                    if (bytes > 0) result[desc.Trim()] = bytes;
                }
                catch (System.Security.SecurityException) { /* some subkeys are locked */ }
            }
        }
        catch (Exception) { /* ignore, fallback to WMI value */ }
        return result;
    }

    // SMBIOS chassis types (DMTF standard) that mean "portable device".
    // 8 Portable, 9 Laptop, 10 Notebook, 11 Hand Held, 14 Sub Notebook,
    // 30 Tablet, 31 Convertible, 32 Detachable.
    private static readonly HashSet<int> PortableChassis = new() { 8, 9, 10, 11, 14, 30, 31, 32 };

    // 3 Desktop, 4 Low Profile Desktop, 5 Pizza Box, 6 Mini Tower, 7 Tower,
    // 13 All in One, 15 Space-saving, 16 Lunch Box, 35 Mini PC, 36 Stick PC.
    private static readonly HashSet<int> DesktopChassis = new() { 3, 4, 5, 6, 7, 13, 15, 16, 35, 36 };

    /// <summary>
    /// Laptop or desktop? The firmware (SMBIOS) reports a "chassis type" for
    /// the case of the computer. It's the most reliable source, but some cheap
    /// boards report "Other" (1) or "Unknown" (2) — then a battery means laptop.
    /// </summary>
    public static DeviceKind DetectDeviceKind(IReadOnlyCollection<int> chassisTypes, bool hasBattery)
    {
        if (chassisTypes.Any(PortableChassis.Contains)) return DeviceKind.Laptop;
        // A desktop can report a "battery" too — a UPS connected over USB — so
        // a desktop chassis wins over the battery check.
        if (chassisTypes.Any(DesktopChassis.Contains)) return DeviceKind.Desktop;
        return hasBattery ? DeviceKind.Laptop : DeviceKind.Desktop;
    }

    private List<int> ReadChassisTypes()
    {
        var result = new List<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject enclosure in searcher.Get())
            {
                if (enclosure["ChassisTypes"] is ushort[] types)
                    result.AddRange(types.Select(t => (int)t));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Chassis type query failed"); }
        return result;
    }

    /// <summary>
    /// Heuristic: decides by name whether the GPU is built into the CPU.
    /// Integrated GPUs share system RAM and are much weaker, so the tier
    /// calculation treats them differently.
    /// </summary>
    public static bool IsIntegratedGpu(string name)
    {
        string n = name.ToLowerInvariant();

        if (n.Contains("intel"))
            return !n.Contains("arc a") && !n.Contains("arc(tm) a"); // Arc A-series cards are discrete

        if (n.Contains("amd") || n.Contains("radeon"))
        {
            // Discrete AMD cards are "RX ..." or "Pro ...". Integrated ones are
            // "Radeon Graphics", "Radeon Vega 8", "Radeon 780M", etc.
            if (n.Contains(" rx ") || n.Contains("rx 5") || n.Contains("rx 6") || n.Contains("rx 7") || n.Contains("rx 9"))
                return false;
            return true;
        }

        return false; // NVIDIA and everything else: discrete
    }

    private void ReadRam(SystemInfo info)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject cs in searcher.Get())
            {
                ulong bytes = Convert.ToUInt64(cs["TotalPhysicalMemory"] ?? 0ul);
                info.RamGb = Math.Round(bytes / 1024.0 / 1024 / 1024, 1);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "RAM query failed"); }
    }

    /// <summary>
    /// Finds the physical disk that holds drive C: and checks if it is an SSD.
    /// Step 1: MSFT_Partition gives the DiskNumber for drive letter C.
    /// Step 2: MSFT_PhysicalDisk with that DeviceId gives MediaType (3 = HDD, 4 = SSD).
    /// </summary>
    private void ReadSystemDrive(SystemInfo info)
    {
        try
        {
            char letter = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")[0];
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");

            uint? diskNumber = null;
            using (var partitions = new ManagementObjectSearcher(scope,
                       new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'")))
            {
                foreach (ManagementObject p in partitions.Get())
                    diskNumber = Convert.ToUInt32(p["DiskNumber"]);
            }
            if (diskNumber == null) return;

            using var disks = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'"));
            foreach (ManagementObject d in disks.Get())
            {
                int mediaType = Convert.ToInt32(d["MediaType"] ?? 0);
                info.SystemDriveIsSsd = mediaType switch
                {
                    4 => true,
                    3 => false,
                    _ => null
                };
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Disk type query failed"); }
    }

    private void ReadBattery(SystemInfo info)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            info.HasBattery = searcher.Get().Count > 0;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Battery query failed"); }
    }
}
