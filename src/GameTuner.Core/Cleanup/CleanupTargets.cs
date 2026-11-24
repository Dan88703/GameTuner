using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameTuner.Core.Cleanup;

/// <summary>What a scan found in one category.</summary>
public sealed record CleanupScanResult(long Bytes, int Files, string? Note = null)
{
    /// <summary>Size can't be known before cleaning (e.g. DISM).</summary>
    public bool SizeUnknown { get; init; }
}

/// <summary>What cleaning one category did.</summary>
public sealed record CleanupResult(long FreedBytes, int Deleted, int Skipped, string? Note = null);

/// <summary>One kind of junk ("Temporary files", "Browser cache", …).</summary>
public abstract class CleanupTarget
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>Needs admin rights (system folders under C:\Windows, C:\ProgramData).</summary>
    public virtual bool RequiresAdmin => false;

    /// <summary>Ticked by default. Off for anything with a side effect the user should choose.</summary>
    public virtual bool SelectedByDefault => true;

    /// <summary>Shown under the item when cleaning it has a side effect.</summary>
    public virtual string? Warning => null;

    public abstract CleanupScanResult Scan(CancellationToken token);
    public abstract CleanupResult Clean(CancellationToken token);
}

/// <summary>
/// Deletes files inside one or more folders.
///
/// Safety rules (the reason this class exists instead of a simple Directory.Delete):
///   - the listed folders themselves are never deleted, only what is inside;
///   - reparse points (symlinks, junctions) are never followed: a junction
///     inside Temp could point to your Documents;
///   - files newer than <see cref="MinAge"/> are kept (an installer may be using them right now);
///   - files that are locked or access-denied are skipped, not forced;
///   - if one of <see cref="BlockingProcesses"/> runs (e.g. the browser whose
///     cache this is), the whole category is skipped.
/// Paths may contain "*" segments: "...\User Data\*\Cache" matches every browser profile.
/// </summary>
public class FolderCleanupTarget : CleanupTarget
{
    private readonly string _id, _name, _description;

    public FolderCleanupTarget(string id, string name, string description, IEnumerable<string> folders)
    {
        _id = id;
        _name = name;
        _description = description;
        Folders = folders.ToList();
    }

    public override string Id => _id;
    public override string Name => _name;
    public override string Description => _description;

    public IReadOnlyList<string> Folders { get; }

    /// <summary>Single files to delete as well (e.g. C:\Windows\MEMORY.DMP).</summary>
    public IReadOnlyList<string> ExtraFiles { get; init; } = Array.Empty<string>();

    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
    public IReadOnlyList<string> BlockingProcesses { get; init; } = Array.Empty<string>();
    public bool Admin { get; init; }
    public bool Default { get; init; } = true;
    public string? WarningText { get; init; }

    public override bool RequiresAdmin => Admin;
    public override bool SelectedByDefault => Default;
    public override string? Warning => WarningText;

    public override CleanupScanResult Scan(CancellationToken token)
    {
        long bytes = 0;
        int files = 0;
        foreach (var file in EnumerateCandidates(token))
        {
            bytes += file.Length;
            files++;
        }
        return new CleanupScanResult(bytes, files, BlockingNote());
    }

    public override CleanupResult Clean(CancellationToken token)
    {
        if (BlockingNote() is string note) return new CleanupResult(0, 0, 0, note);

        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var file in EnumerateCandidates(token).ToList())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                long size = file.Length;
                // Read-only files can't be deleted until the attribute is cleared.
                if (file.IsReadOnly) file.IsReadOnly = false;
                file.Delete();
                freed += size;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;   // in use or protected: leave it
            }
        }

        foreach (string root in ExpandFolders()) RemoveEmptySubfolders(root);
        return new CleanupResult(freed, deleted, skipped);
    }

    /// <summary>Every file that may be deleted, after all safety filters.</summary>
    internal IEnumerable<FileInfo> EnumerateCandidates(CancellationToken token)
    {
        DateTime cutoff = DateTime.UtcNow - MinAge;

        foreach (string root in ExpandFolders())
        {
            foreach (var file in EnumerateFilesSafe(new DirectoryInfo(root), token))
            {
                if (MinAge > TimeSpan.Zero && file.LastWriteTimeUtc > cutoff) continue;
                yield return file;
            }
        }

        foreach (string path in ExtraFiles)
        {
            var file = new FileInfo(Environment.ExpandEnvironmentVariables(path));
            if (file.Exists) yield return file;
        }
    }

    /// <summary>
    /// Recursive walk written by hand instead of SearchOption.AllDirectories,
    /// because the built-in version follows junctions and throws on the first
    /// folder it can't open; this one skips both.
    /// </summary>
    private static IEnumerable<FileInfo> EnumerateFilesSafe(DirectoryInfo dir, CancellationToken token)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();

            FileSystemInfo[] entries;
            try { entries = current.GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;   // never follow links
                if (entry is DirectoryInfo sub) stack.Push(sub);
                else if (entry is FileInfo f) yield return f;
            }
        }
    }

    private static void RemoveEmptySubfolders(string root)
    {
        DirectoryInfo[] subs;
        try { subs = new DirectoryInfo(root).GetDirectories(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var sub in subs)
        {
            if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            RemoveEmptySubfolders(sub.FullName);
            try
            {
                if (!sub.EnumerateFileSystemInfos().Any()) sub.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Expands environment variables and "*" path segments into existing folders.</summary>
    internal IEnumerable<string> ExpandFolders() =>
        Folders.SelectMany(f => ExpandPattern(Environment.ExpandEnvironmentVariables(f))).Distinct(StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<string> ExpandPattern(string path)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Array.Empty<string>();

        // Start from the drive ("C:" → "C:\") and walk segment by segment.
        IEnumerable<string> current = new[] { parts[0].EndsWith(':') ? parts[0] + Path.DirectorySeparatorChar : parts[0] };
        foreach (string part in parts.Skip(1))
        {
            current = part.Contains('*')
                ? current.SelectMany(dir => SafeGetDirectories(dir, part))
                : current.Select(dir => Path.Combine(dir, part));
        }
        return current.Where(Directory.Exists);
    }

    private static IEnumerable<string> SafeGetDirectories(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private string? BlockingNote()
    {
        foreach (string name in BlockingProcesses)
        {
            var running = Process.GetProcessesByName(name);
            foreach (var p in running) p.Dispose();
            if (running.Length > 0) return $"Закрой {name}, чтобы очистить этот пункт";
        }
        return null;
    }
}

/// <summary>
/// The Recycle Bin of all drives, through the Windows shell API — the same
/// thing as right-click → "Empty Recycle Bin", just without the dialog.
/// </summary>
public sealed class RecycleBinTarget : CleanupTarget
{
    public override string Id => "recycle-bin";
    public override string Name => "Корзина";
    public override string Description => "Файлы, которые ты уже удалил, но они всё ещё занимают место.";
    public override bool SelectedByDefault => false;
    public override string? Warning => "Файлы из корзины будут удалены навсегда.";

    public override CleanupScanResult Scan(CancellationToken token)
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        // null root = all drives
        return SHQueryRecycleBin(null, ref info) == 0
            ? new CleanupScanResult(info.i64Size, (int)Math.Min(info.i64NumItems, int.MaxValue))
            : new CleanupScanResult(0, 0, "Не удалось прочитать корзину");
    }

    public override CleanupResult Clean(CancellationToken token)
    {
        var before = Scan(token);
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        // E_UNEXPECTED (0x8000FFFF) is returned when the bin is already empty.
        return hr == 0 || before.Files == 0
            ? new CleanupResult(before.Bytes, before.Files, 0)
            : new CleanupResult(0, 0, before.Files, $"Windows вернул ошибку 0x{hr:X8}");
    }

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}

/// <summary>
/// Windows component store cleanup: removes old versions of system files that
/// were replaced by updates (DISM /StartComponentCleanup). Windows does this
/// itself once in a while; running it by hand often frees several GB.
/// After it, already installed updates can no longer be uninstalled.
/// </summary>
public sealed class ComponentStoreTarget : CleanupTarget
{
    public override string Id => "component-store";
    public override string Name => "Старые компоненты Windows";
    public override string Description => "Старые версии системных файлов, заменённые обновлениями (DISM).";
    public override bool RequiresAdmin => true;
    public override bool SelectedByDefault => false;
    public override string? Warning => "Занимает 5–15 минут. После неё нельзя будет удалить уже установленные обновления Windows.";

    public override CleanupScanResult Scan(CancellationToken token) =>
        new(0, 0, "Размер станет известен после очистки") { SizeUnknown = true };

    public override CleanupResult Clean(CancellationToken token)
    {
        using var p = Process.Start(new ProcessStartInfo("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /Quiet")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (p == null) return new CleanupResult(0, 0, 0, "Не удалось запустить DISM");

        // Poll so the user's "cancel" still works while DISM runs.
        while (!p.WaitForExit(1000))
        {
            if (token.IsCancellationRequested)
            {
                try { p.Kill(); } catch { }
                token.ThrowIfCancellationRequested();
            }
        }
        // Freed space is measured by the caller from the drive's free space.
        return new CleanupResult(0, 0, 0, p.ExitCode == 0 ? null : $"DISM завершился с кодом {p.ExitCode}");
    }
}

/// <summary>The list of everything GameTuner can clean, in display order.</summary>
public static class CleanupCatalog
{
    private static readonly string[] Browsers = { "chrome", "msedge", "firefox", "opera", "brave" };

    public static IReadOnlyList<CleanupTarget> Create() => new List<CleanupTarget>
    {
        new FolderCleanupTarget("user-temp", "Временные файлы",
            "Временные файлы программ и установщиков в твоей папке Temp (старше суток).",
            new[] { "%TEMP%" })
        { MinAge = TimeSpan.FromDays(1) },

        new FolderCleanupTarget("windows-temp", "Временные файлы Windows",
            @"Системная папка C:\Windows\Temp (старше суток).",
            new[] { @"%WINDIR%\Temp" })
        { MinAge = TimeSpan.FromDays(1), Admin = true },

        new FolderCleanupTarget("windows-update", "Кэш обновлений Windows",
            "Скачанные файлы обновлений, которые уже установлены. При необходимости Windows скачает их заново.",
            new[] { @"%WINDIR%\SoftwareDistribution\Download" })
        { Admin = true },

        new FolderCleanupTarget("delivery-optimization", "Кэш оптимизации доставки",
            "Копии обновлений, которые Windows раздаёт другим компьютерам.",
            new[] { @"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache" })
        { Admin = true },

        new FolderCleanupTarget("browser-cache", "Кэш браузеров",
            "Сохранённые копии сайтов в Chrome, Edge, Firefox, Opera, Brave. Пароли, история, вкладки и куки НЕ удаляются.",
            new[]
            {
                @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache",
                @"%LOCALAPPDATA%\Google\Chrome\User Data\*\Code Cache",
                @"%LOCALAPPDATA%\Google\Chrome\User Data\*\GPUCache",
                @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Cache",
                @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Code Cache",
                @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\GPUCache",
                @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\*\Cache",
                @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2",
                @"%LOCALAPPDATA%\Opera Software\*\Cache"
            })
        { BlockingProcesses = Browsers },

        new FolderCleanupTarget("crash-dumps", "Дампы сбоев и отчёты об ошибках",
            "Снимки памяти после вылетов программ и синих экранов. Нужны только разработчикам для отладки.",
            new[]
            {
                @"%LOCALAPPDATA%\CrashDumps",
                @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive",
                @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue",
                @"%WINDIR%\Minidump"
            })
        { Admin = true, ExtraFiles = new[] { @"%WINDIR%\MEMORY.DMP" } },

        new RecycleBinTarget(),

        new FolderCleanupTarget("shader-cache", "Кэш шейдеров видеокарты",
            "Скомпилированные шейдеры DirectX, AMD, NVIDIA, Intel. Помогает, если игра стала статтерить после обновления драйвера.",
            new[]
            {
                @"%LOCALAPPDATA%\D3DSCache",
                @"%LOCALAPPDATA%\AMD\DxCache",
                @"%LOCALAPPDATA%\AMD\DxcCache",
                @"%LOCALAPPDATA%\AMD\GLCache",
                @"%LOCALAPPDATA%\AMD\VkCache",
                @"%LOCALAPPDATA%\NVIDIA\DXCache",
                @"%LOCALAPPDATA%\NVIDIA\GLCache",
                @"%LOCALAPPDATA%\Intel\ShaderCache"
            })
        {
            Default = false,
            WarningText = "Первые запуски игр после очистки будут подлагивать, пока кэш соберётся заново."
        },

        new ComponentStoreTarget()
    };
}
