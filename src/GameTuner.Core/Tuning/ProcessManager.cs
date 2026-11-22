using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Tuning;

public sealed record ProcessEntry(string Name, string Title, double MemoryMb, int Count);

public interface IProcessManager
{
    /// <summary>User apps using at least <paramref name="minMemoryMb"/> MB, grouped by name.</summary>
    IReadOnlyList<ProcessEntry> GetHeavyProcesses(double minMemoryMb = 150);

    /// <summary>Closes all processes with this name. Returns how many were closed.</summary>
    int CloseByName(string name);

    /// <summary>Raises a process to "High" priority. Returns false if Windows refused.</summary>
    bool SetHighPriority(int processId);
}

/// <summary>
/// Lists and closes heavy background apps. Safety rules:
///   - only processes in the current user's session (services run in session 0)
///   - a hard-coded protected list (Windows core, security, drivers, launchers)
///     is never shown and never closed
///   - closing is polite first (like clicking X), forced only if the app hangs
/// </summary>
public sealed class ProcessManager : IProcessManager
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker",
        "SearchHost", "SearchIndexer", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
        "audiodg", "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "spoolsv",
        "dllhost", "conhost", "WmiPrvSE", "Memory Compression", "LockApp", "ApplicationFrameHost",
        "SystemSettings", "GameTuner", "GameTuner.App", "PresentMon", "nvcontainer", "NVDisplay.Container",
        "RadeonSoftware", "AMDRSServ", "igfxEM", "Steam", "steamwebhelper", "EpicGamesLauncher",
        "EpicWebHelper", "Rockstar Launcher", "RockstarService", "EADesktop", "Battle.net", "Discord"
    };

    private readonly ILogger<ProcessManager> _log;

    public ProcessManager(ILogger<ProcessManager>? log = null)
    {
        _log = log ?? NullLogger<ProcessManager>.Instance;
    }

    public static bool IsProtected(string name) =>
        Protected.Contains(name) || name.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ProcessEntry> GetHeavyProcesses(double minMemoryMb = 150)
    {
        int mySession;
        using (var me = Process.GetCurrentProcess()) mySession = me.SessionId;

        var all = Process.GetProcesses();
        try
        {
            // Browsers and Electron apps run dozens of processes with one name,
            // so group by name and sum memory: "chrome — 1800 MB (32)".
            return all
                .Where(p => Safe(() => p.SessionId == mySession, false) && !IsProtected(p.ProcessName))
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ProcessEntry(
                    g.Key,
                    g.Select(p => Safe(() => p.MainWindowTitle, "")).FirstOrDefault(t => t.Length > 0) ?? "",
                    Math.Round(g.Sum(p => Safe(() => p.WorkingSet64, 0L)) / 1024.0 / 1024),
                    g.Count()))
                .Where(e => e.MemoryMb >= minMemoryMb)
                .OrderByDescending(e => e.MemoryMb)
                .ToList();
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    public int CloseByName(string name)
    {
        if (IsProtected(name)) return 0;

        int closed = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    // CloseMainWindow = clicking X: the app can save its data.
                    if (p.CloseMainWindow() && p.WaitForExit(3000))
                    {
                        closed++;
                        continue;
                    }
                    if (!p.HasExited)
                    {
                        p.Kill();
                        closed++;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Could not close {Name} ({Pid})", name, p.Id);
                }
            }
        }
        _log.LogInformation("Closed {Count} process(es) named {Name}", closed, name);
        return closed;
    }

    public bool SetHighPriority(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            // "High" is allowed for the user's own processes without admin.
            // "RealTime" is never used: it can starve input and audio drivers.
            p.PriorityClass = ProcessPriorityClass.High;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not raise priority of {Pid}", processId);
            return false;
        }
    }

    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }
}
