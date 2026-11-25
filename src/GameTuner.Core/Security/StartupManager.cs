using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GameTuner.Core.Tuning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace GameTuner.Core.Security;

/// <summary>Where Windows found the autostart entry.</summary>
public enum StartupSource
{
    UserRun,          // HKCU\...\Run
    MachineRun,       // HKLM\...\Run (64-bit)
    MachineRun32,     // HKLM\...\WOW6432Node\...\Run (32-bit programs)
    UserFolder,       // %APPDATA%\...\Startup
    CommonFolder      // %PROGRAMDATA%\...\StartUp
}

public enum RiskLevel { None = 0, Info = 1, Warning = 2 }

public sealed record StartupEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string? FilePath { get; init; }
    public bool FileExists { get; init; }
    public string? Publisher { get; init; }
    public StartupSource Source { get; init; }
    public bool IsEnabled { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
    public RiskLevel Risk { get; init; }

    /// <summary>HKLM and the common Startup folder belong to all users: changing them needs admin.</summary>
    public bool RequiresAdmin => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;
}

public interface IStartupManager
{
    IReadOnlyList<StartupEntry> GetEntries();

    /// <summary>Enables/disables an entry exactly like Task Manager does. Returns false if Windows refused.</summary>
    bool SetEnabled(StartupEntry entry, bool enabled);
}

/// <summary>
/// Lists and switches autostart programs.
///
/// Disabling is done the same way as Task Manager's "Startup apps" tab: the
/// entry itself is NOT deleted. Windows keeps an on/off flag for every entry in
/// ...\Explorer\StartupApproved\{Run|Run32|StartupFolder}; we write that flag.
/// So everything can be switched back on — in GameTuner or in Task Manager.
///
/// Each entry is also checked by <see cref="StartupRules"/> for signs that are
/// typical for junk or malware (missing file, runs from Temp, script hosts,
/// unsigned files in AppData). This is a hint for the user, not a verdict:
/// real malware detection is Defender's job.
/// </summary>
public sealed class StartupManager : IStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private readonly ILogger<StartupManager> _log;

    public StartupManager(ILogger<StartupManager>? log = null)
    {
        _log = log ?? NullLogger<StartupManager>.Instance;
    }

    public IReadOnlyList<StartupEntry> GetEntries()
    {
        var entries = new List<StartupEntry>();
        ReadRunKey(entries, Registry.CurrentUser, RunKey, StartupSource.UserRun);
        ReadRunKey(entries, Registry.LocalMachine, RunKey, StartupSource.MachineRun);
        ReadRunKey(entries, Registry.LocalMachine, Run32Key, StartupSource.MachineRun32);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder);

        return entries
            .OrderByDescending(e => e.Risk)
            .ThenByDescending(e => e.IsEnabled)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public bool SetEnabled(StartupEntry entry, bool enabled)
    {
        if (entry.RequiresAdmin && !Elevation.IsElevated) return false;
        try
        {
            var (hive, subKey) = ApprovedLocation(entry.Source);
            using var key = hive.CreateSubKey(subKey);
            key.SetValue(entry.Name, ApprovedValue(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
            _log.LogInformation("Startup entry {Name} {State}", entry.Name, enabled ? "enabled" : "disabled");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not change startup entry {Name}", entry.Name);
            return false;
        }
    }

    // ------------------------------------------------------------------

    private void ReadRunKey(List<StartupEntry> list, RegistryKey hive, string path, StartupSource source)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key == null) return;
            foreach (string name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string command || command.Length == 0) continue;
                list.Add(Build(name, command, StartupRules.ExtractPath(command), source));
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Cannot read {Path}", path); }
    }

    private void ReadFolder(List<StartupEntry> list, string folder, StartupSource source)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string file in Directory.GetFiles(folder))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                // Shortcuts point somewhere else: check the target, not the .lnk itself.
                string target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ResolveShortcut(file) ?? file
                    : file;
                list.Add(Build(fileName, target, target, source));
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Cannot read {Folder}", folder); }
    }

    private StartupEntry Build(string name, string command, string? path, StartupSource source)
    {
        bool exists = path != null && File.Exists(path);
        string? publisher = exists ? GetPublisher(path!) : null;
        var (findings, risk) = StartupRules.Evaluate(command, path, exists, publisher);

        return new StartupEntry
        {
            Name = name,
            Command = command,
            FilePath = path,
            FileExists = exists,
            Publisher = publisher,
            Source = source,
            IsEnabled = ReadApproved(source, name),
            Findings = findings,
            Risk = risk
        };
    }

    /// <summary>
    /// The StartupApproved value is 12 bytes: a 4-byte state + an 8-byte FILETIME
    /// (when it was disabled). Windows uses even numbers (2, 6) for "enabled" and
    /// odd numbers (3, 7) for "disabled". A missing value means enabled.
    /// </summary>
    internal static bool IsApprovedEnabled(byte[]? value) => value == null || value.Length == 0 || (value[0] & 1) == 0;

    internal static byte[] ApprovedValue(bool enabled, DateTime nowUtc)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(nowUtc.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }

    private static bool ReadApproved(StartupSource source, string name)
    {
        try
        {
            var (hive, subKey) = ApprovedLocation(source);
            using var key = hive.OpenSubKey(subKey);
            return IsApprovedEnabled(key?.GetValue(name) as byte[]);
        }
        catch (Exception) { return true; }
    }

    private static (RegistryKey Hive, string SubKey) ApprovedLocation(StartupSource source) => source switch
    {
        StartupSource.UserRun => (Registry.CurrentUser, ApprovedKey + @"\Run"),
        StartupSource.MachineRun => (Registry.LocalMachine, ApprovedKey + @"\Run"),
        StartupSource.MachineRun32 => (Registry.LocalMachine, ApprovedKey + @"\Run32"),
        StartupSource.UserFolder => (Registry.CurrentUser, ApprovedKey + @"\StartupFolder"),
        _ => (Registry.LocalMachine, ApprovedKey + @"\StartupFolder")
    };

    /// <summary>
    /// Publisher from the file's Authenticode signature, or null if unsigned.
    /// Note: some Windows files are "catalog-signed" (the signature is stored
    /// elsewhere), so they look unsigned here — StartupRules ignores missing
    /// signatures for files inside the Windows folder for that reason.
    /// </summary>
    private static string? GetPublisher(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // simplest API to read an embedded Authenticode certificate
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (CryptographicException) { return null; }
        catch (Exception) { return null; }
    }

    /// <summary>Reads a .lnk target through the same WScript.Shell COM object used for creating shortcuts.</summary>
    private static string? ResolveShortcut(string lnk)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(lnk);
            try
            {
                string target = link.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        catch (Exception) { return null; }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}

/// <summary>
/// Pure rules that look at an autostart command and point out suspicious signs.
/// Kept separate (and static) so they can be unit-tested without the registry.
/// </summary>
public static class StartupRules
{
    // Programs that run scripts. Legit apps rarely start through them; malware often does.
    private static readonly string[] ScriptHosts = { "wscript", "cscript", "mshta", "powershell", "pwsh" };
    private static readonly string[] ScriptExtensions = { ".vbs", ".vbe", ".js", ".jse", ".bat", ".cmd", ".ps1", ".hta", ".wsf" };

    /// <summary>
    /// Extracts the program path from a command line:
    ///   "C:\Program Files\App\app.exe" --minimized  →  C:\Program Files\App\app.exe
    ///   C:\Tools\tool.exe /silent                    →  C:\Tools\tool.exe
    ///   %LOCALAPPDATA%\x\y.exe                        →  C:\Users\…\AppData\Local\x\y.exe
    /// </summary>
    public static string? ExtractPath(string command)
    {
        string? path = ExtractRaw(command);
        return path == null ? null : ResolveBareName(path);
    }

    private static string? ExtractRaw(string command)
    {
        string cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        if (cmd.Length == 0) return null;

        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : cmd.Trim('"');
        }

        // Unquoted: cut after the first known executable extension (paths may contain spaces).
        foreach (string ext in new[] { ".exe", ".com", ".bat", ".cmd", ".vbs", ".js", ".ps1", ".lnk" })
        {
            int idx = cmd.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return cmd[..(idx + ext.Length)];
        }
        int space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
    }

    /// <summary>
    /// "rundll32.exe …" has no folder: Windows finds it in System32 / PATH, so
    /// we must too — otherwise it would be wrongly reported as "file not found".
    /// </summary>
    private static string ResolveBareName(string path)
    {
        if (Path.IsPathRooted(path)) return path;

        string name = Path.HasExtension(path) ? path : path + ".exe";
        var folders = new List<string> { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (string folder in folders)
        {
            try
            {
                string candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return path;
    }

    public static (IReadOnlyList<string> Findings, RiskLevel Risk) Evaluate(
        string command, string? path, bool fileExists, string? publisher)
    {
        var findings = new List<string>();
        var risk = RiskLevel.None;
        void Add(string text, RiskLevel level)
        {
            findings.Add(text);
            if (level > risk) risk = level;
        }

        string lowerCmd = command.ToLowerInvariant();
        string lowerPath = (path ?? "").ToLowerInvariant();
        string temp = Path.GetTempPath().ToLowerInvariant();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).ToLowerInvariant();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).ToLowerInvariant();

        if (!fileExists)
            Add("Файл не найден — скорее всего, остаток удалённой программы", RiskLevel.Warning);

        if (lowerPath.Length > 0 && temp.Length > 0 && lowerPath.StartsWith(temp))
            Add("Запускается из папки Temp — так часто делают вредоносные программы", RiskLevel.Warning);

        string? host = ScriptHosts.FirstOrDefault(h =>
            Path.GetFileNameWithoutExtension(lowerPath) == h || lowerCmd.StartsWith(h + " ") || lowerCmd.Contains("\\" + h + ".exe"));
        if (host != null)
            Add($"Запускает скрипт через {host}", RiskLevel.Warning);

        if (lowerCmd.Contains("-enc ") || lowerCmd.Contains("-encodedcommand"))
            Add("Скрытая (закодированная) команда PowerShell", RiskLevel.Warning);

        if (ScriptExtensions.Any(e => lowerPath.EndsWith(e)))
            Add("Автозапуск скрипта, а не программы", RiskLevel.Warning);

        bool inWindows = windows.Length > 0 && lowerPath.StartsWith(windows);
        bool inUserAppData = (appData.Length > 0 && lowerPath.StartsWith(appData)) ||
                             (localAppData.Length > 0 && lowerPath.StartsWith(localAppData));

        if (fileExists && publisher == null && !inWindows)
        {
            Add(inUserAppData
                    ? "Нет цифровой подписи и лежит в AppData — проверь, знаешь ли ты эту программу"
                    : "Нет цифровой подписи",
                inUserAppData ? RiskLevel.Warning : RiskLevel.Info);
        }

        return (findings, risk);
    }
}
