using System.Diagnostics;
using System.Management;
using GameTuner.Core.Tuning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Security;

/// <summary>State of the antivirus protection on this PC.</summary>
public sealed record ProtectionStatus
{
    /// <summary>Microsoft Defender answered at all (false = removed, broken, or replaced).</summary>
    public bool DefenderAvailable { get; init; }

    /// <summary>Defender is the active antivirus (not "passive" behind another one).</summary>
    public bool DefenderActive { get; init; }

    public bool RealTimeProtection { get; init; }
    public DateTime? SignaturesUpdated { get; init; }
    public int? DaysSinceQuickScan { get; init; }
    public int? DaysSinceFullScan { get; init; }

    /// <summary>Other antivirus products registered in Windows Security Center (e.g. "Kaspersky").</summary>
    public IReadOnlyList<string> OtherAntivirus { get; init; } = Array.Empty<string>();

    public bool SignaturesOutdated =>
        SignaturesUpdated is DateTime d && DateTime.Now - d > TimeSpan.FromDays(3);
}

public enum ThreatSeverity { Unknown = 0, Low = 1, Moderate = 2, High = 4, Severe = 5 }

/// <summary>One threat Defender has found (now or in the past).</summary>
public sealed record ThreatInfo(
    long Id,
    string Name,
    ThreatSeverity Severity,
    bool IsActive,                 // still on the PC and not quarantined
    DateTime? DetectedAt,
    IReadOnlyList<string> Files);

public enum ScanKind { Quick = 1, Full = 2 }

public interface IDefenderService
{
    Task<ProtectionStatus> GetStatusAsync();
    Task<IReadOnlyList<ThreatInfo>> GetThreatsAsync();
    Task<bool> UpdateSignaturesAsync(CancellationToken token = default);

    /// <summary>Runs a Defender scan. Returns true if threats were found.</summary>
    Task<bool> ScanAsync(ScanKind kind, CancellationToken token = default);

    /// <summary>Asks Defender to remove every active threat.</summary>
    Task<bool> RemoveActiveThreatsAsync();
}

/// <summary>
/// GameTuner's front-end for Microsoft Defender — the antivirus built into Windows.
///
/// GameTuner never decides on its own what is malware: detection and removal
/// are done by Defender's engine and its daily-updated signature database.
/// This class only:
///   - READS state through WMI (namespace root\Microsoft\Windows\Defender,
///     the same data the Get-MpComputerStatus / Get-MpThreat cmdlets show);
///   - RUNS actions through MpCmdRun.exe, Defender's official command-line tool;
///   - removes threats with the Remove-MpThreat cmdlet.
/// Scans, updates and removal need admin rights.
/// </summary>
public sealed class DefenderService : IDefenderService
{
    private const string DefenderNamespace = @"\\.\root\Microsoft\Windows\Defender";
    private readonly ILogger<DefenderService> _log;

    public DefenderService(ILogger<DefenderService>? log = null)
    {
        _log = log ?? NullLogger<DefenderService>.Instance;
    }

    public Task<ProtectionStatus> GetStatusAsync() => Task.Run(() =>
    {
        var others = ReadOtherAntivirus();
        try
        {
            using var searcher = new ManagementObjectSearcher(DefenderNamespace,
                "SELECT AntivirusEnabled, RealTimeProtectionEnabled, AMRunningMode, AntivirusSignatureLastUpdated, QuickScanAge, FullScanAge FROM MSFT_MpComputerStatus");
            foreach (ManagementObject s in searcher.Get())
            {
                string mode = s["AMRunningMode"] as string ?? "Normal";
                // QuickScanAge/FullScanAge = days; 4294967295 (uint max) means "never".
                static int? Age(object? v) => v is uint u && u < 100_000 ? (int)u : null;

                return new ProtectionStatus
                {
                    DefenderAvailable = true,
                    DefenderActive = s["AntivirusEnabled"] is true && !mode.Contains("Passive", StringComparison.OrdinalIgnoreCase),
                    RealTimeProtection = s["RealTimeProtectionEnabled"] is true,
                    SignaturesUpdated = ParseDate(s["AntivirusSignatureLastUpdated"]),
                    DaysSinceQuickScan = Age(s["QuickScanAge"]),
                    DaysSinceFullScan = Age(s["FullScanAge"]),
                    OtherAntivirus = others
                };
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read Defender status");
        }
        return new ProtectionStatus { DefenderAvailable = false, OtherAntivirus = others };
    });

    public Task<IReadOnlyList<ThreatInfo>> GetThreatsAsync() => Task.Run<IReadOnlyList<ThreatInfo>>(() =>
    {
        var result = new List<ThreatInfo>();
        try
        {
            // MSFT_MpThreat = what the threat is; MSFT_MpThreatDetection = where/when it was found.
            var detections = new Dictionary<long, (DateTime? When, List<string> Files)>();
            using (var det = new ManagementObjectSearcher(DefenderNamespace,
                       "SELECT ThreatID, InitialDetectionTime, Resources FROM MSFT_MpThreatDetection"))
            {
                foreach (ManagementObject d in det.Get())
                {
                    long id = Convert.ToInt64(d["ThreatID"]);
                    var files = (d["Resources"] as string[] ?? Array.Empty<string>())
                        .Select(CleanResource).ToList();
                    if (detections.TryGetValue(id, out var existing)) existing.Files.AddRange(files);
                    else detections[id] = (ParseDate(d["InitialDetectionTime"]), files);
                }
            }

            using var threats = new ManagementObjectSearcher(DefenderNamespace,
                "SELECT ThreatID, ThreatName, SeverityID, IsActive FROM MSFT_MpThreat");
            foreach (ManagementObject t in threats.Get())
            {
                long id = Convert.ToInt64(t["ThreatID"]);
                detections.TryGetValue(id, out var d);
                byte severity = Convert.ToByte(t["SeverityID"] ?? (byte)0);
                result.Add(new ThreatInfo(
                    id,
                    t["ThreatName"] as string ?? "Неизвестная угроза",
                    Enum.IsDefined(typeof(ThreatSeverity), (int)severity) ? (ThreatSeverity)severity : ThreatSeverity.Unknown,
                    t["IsActive"] is true,
                    d.When,
                    d.Files?.Distinct().ToList() ?? new List<string>()));
            }
        }
        catch (Exception ex)
        {
            // Reading threats needs admin; without it we simply show nothing.
            _log.LogInformation(ex, "Could not read Defender threats");
        }
        return result.OrderByDescending(t => t.IsActive).ThenByDescending(t => t.DetectedAt).ToList();
    });

    public async Task<bool> UpdateSignaturesAsync(CancellationToken token = default) =>
        await RunMpCmdRunAsync("-SignatureUpdate", token) == 0;

    public async Task<bool> ScanAsync(ScanKind kind, CancellationToken token = default)
    {
        // MpCmdRun exit codes: 0 = clean, 2 = threats found (and handled by Defender's actions).
        int code = await RunMpCmdRunAsync($"-Scan -ScanType {(int)kind}", token);
        return code == 2;
    }

    public Task<bool> RemoveActiveThreatsAsync() => Task.Run(() =>
    {
        // Remove-MpThreat removes all ACTIVE threats using Defender's own remediation.
        int code = RunProcess("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Remove-MpThreat\"",
            TimeSpan.FromMinutes(5), CancellationToken.None);
        return code == 0;
    });

    // ------------------------------------------------------------------

    private Task<int> RunMpCmdRunAsync(string args, CancellationToken token) => Task.Run(() =>
    {
        string? exe = FindMpCmdRun();
        if (exe == null)
        {
            _log.LogWarning("MpCmdRun.exe not found");
            return -1;
        }

        int code = RunProcess(exe, args, TimeSpan.FromHours(6), token);
        if (code == int.MinValue)
        {
            // Cancelled: stop the scan inside Defender too, not only our child process.
            RunProcess(exe, "-Scan -Cancel", TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        _log.LogInformation("MpCmdRun {Args} exited with {Code}", args, code);
        return code;
    }, CancellationToken.None);

    /// <summary>Runs a process hidden and waits. int.MinValue = cancelled, -1 = failed to start.</summary>
    private int RunProcess(string exe, string args, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return -1;

            // Drain output in the background so a full pipe can't block the process.
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();

            var deadline = DateTime.UtcNow + timeout;
            while (!p.WaitForExit(500))
            {
                if (token.IsCancellationRequested || DateTime.UtcNow > deadline)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return int.MinValue;
                }
            }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run {Exe}", exe);
            return -1;
        }
    }

    /// <summary>
    /// Defender updates itself into %ProgramData%\Microsoft\Windows Defender\Platform\&lt;version&gt;.
    /// The newest platform folder has the newest MpCmdRun; the copy in Program Files is the fallback.
    /// </summary>
    internal static string? FindMpCmdRun()
    {
        string platform = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows Defender", "Platform");
        try
        {
            if (Directory.Exists(platform))
            {
                string? newest = Directory.GetDirectories(platform)
                    .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                    .Where(x => x.Version != null && File.Exists(Path.Combine(x.Dir, "MpCmdRun.exe")))
                    .OrderByDescending(x => x.Version)
                    .Select(x => Path.Combine(x.Dir, "MpCmdRun.exe"))
                    .FirstOrDefault();
                if (newest != null) return newest;
            }
        }
        catch (Exception) { /* fall back below */ }

        string classic = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(classic) ? classic : null;
    }

    /// <summary>Other antivirus products from Windows Security Center (no admin needed).</summary>
    private List<string> ReadOtherAntivirus()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"\\.\root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct");
            foreach (ManagementObject av in searcher.Get())
            {
                string name = av["displayName"] as string ?? "";
                if (name.Length > 0 && !name.Contains("Defender", StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        catch (Exception) { /* Security Center is missing on Windows Server */ }
        return names.Distinct().ToList();
    }

    private static DateTime? ParseDate(object? value) => value switch
    {
        DateTime d => d,
        string s when s.Length > 0 => TryDmtf(s),
        _ => null
    };

    private static DateTime? TryDmtf(string s)
    {
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch (Exception) { return null; }
    }

    /// <summary>Defender lists resources like "file:_C:\Users\...\bad.exe" — keep only the path.</summary>
    internal static string CleanResource(string resource)
    {
        int idx = resource.IndexOf(":_", StringComparison.Ordinal);
        return idx >= 0 ? resource[(idx + 2)..] : resource;
    }

    public static string SeverityText(ThreatSeverity s) => s switch
    {
        ThreatSeverity.Low => "низкая",
        ThreatSeverity.Moderate => "средняя",
        ThreatSeverity.High => "высокая",
        ThreatSeverity.Severe => "критическая",
        _ => "неизвестная"
    };

    /// <summary>Opens Windows Security (for things only the user can switch, like real-time protection).</summary>
    public static void OpenWindowsSecurity() =>
        Process.Start(new ProcessStartInfo("windowsdefender://threat") { UseShellExecute = true });
}
