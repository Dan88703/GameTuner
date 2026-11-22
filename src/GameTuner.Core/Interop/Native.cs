using System.Runtime.InteropServices;

namespace GameTuner.Core.Interop;

/// <summary>
/// Direct calls into Windows DLLs (P/Invoke). Used where .NET has no
/// built-in API, and to avoid extra NuGet packages for simple counters.
/// </summary>
internal static class Native
{
    // ---- CPU time (kernel32) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    /// <summary>Total idle / kernel / user time of all cores since boot.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    // ---- Memory (kernel32) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;          // % of RAM in use
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    // ---- Power (kernel32) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 = battery, 1 = charger, 255 = unknown
        public byte BatteryFlag;
        public byte BatteryLifePercent;  // 0..100, 255 = unknown
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // ---- Windows 10/11 "Power mode" slider (powrprof) ----
    // These functions are undocumented but used by the Settings app itself.
    // The overlay is the "Best performance / Balanced / Best efficiency" slider
    // that modern laptops show instead of classic power plans.
    [DllImport("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]
    public static extern uint PowerGetEffectiveOverlayScheme(out Guid overlayGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
    public static extern uint PowerSetActiveOverlayScheme(Guid overlayGuid);
}
