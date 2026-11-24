using GameTuner.Core.Tuning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Cleanup;

/// <summary>Free/total space of the system drive.</summary>
public sealed record DriveSpace(string Name, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;
}

/// <summary>Result of cleaning several categories.</summary>
public sealed record CleanupSummary(
    long FreedBytes,           // measured by the drive's free space: the real number
    int Deleted,
    int Skipped,
    IReadOnlyList<string> Notes);

public interface IDiskCleanupService
{
    IReadOnlyList<CleanupTarget> Targets { get; }
    DriveSpace GetSystemDrive();
    Task<IReadOnlyDictionary<string, CleanupScanResult>> ScanAsync(IProgress<string>? progress = null, CancellationToken token = default);
    Task<CleanupSummary> CleanAsync(IEnumerable<string> targetIds, IProgress<string>? progress = null, CancellationToken token = default);
}

/// <summary>
/// Runs the cleanup targets on a background thread and reports progress.
///
/// "Freed" is measured as the change of free space on drive C: before vs after,
/// not just the sum of deleted file sizes — so the number the user sees is what
/// they really got back (it also counts DISM, whose size can't be predicted).
/// </summary>
public sealed class DiskCleanupService : IDiskCleanupService
{
    private readonly ILogger<DiskCleanupService> _log;

    public DiskCleanupService(ILogger<DiskCleanupService>? log = null, IReadOnlyList<CleanupTarget>? targets = null)
    {
        _log = log ?? NullLogger<DiskCleanupService>.Instance;
        Targets = targets ?? CleanupCatalog.Create();
    }

    public IReadOnlyList<CleanupTarget> Targets { get; }

    public DriveSpace GetSystemDrive()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var drive = new DriveInfo(root);
        return new DriveSpace(root.TrimEnd('\\'), drive.TotalSize, drive.AvailableFreeSpace);
    }

    public Task<IReadOnlyDictionary<string, CleanupScanResult>> ScanAsync(IProgress<string>? progress = null, CancellationToken token = default) =>
        Task.Run<IReadOnlyDictionary<string, CleanupScanResult>>(() =>
        {
            var results = new Dictionary<string, CleanupScanResult>();
            foreach (var target in Targets)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"Сканирую: {target.Name}…");
                try
                {
                    results[target.Id] = target.Scan(token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Scan failed for {Target}", target.Id);
                    results[target.Id] = new CleanupScanResult(0, 0, "Не удалось просканировать");
                }
            }
            return results;
        }, token);

    public Task<CleanupSummary> CleanAsync(IEnumerable<string> targetIds, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var ids = targetIds.ToHashSet();
        return Task.Run(() =>
        {
            long freeBefore = GetSystemDrive().FreeBytes;
            int deleted = 0, skipped = 0;
            var notes = new List<string>();

            foreach (var target in Targets.Where(t => ids.Contains(t.Id)))
            {
                token.ThrowIfCancellationRequested();

                // Second line of defence: the UI disables these, but never trust the UI.
                if (target.RequiresAdmin && !Elevation.IsElevated)
                {
                    notes.Add($"{target.Name}: нужен режим администратора");
                    continue;
                }

                progress?.Report($"Очищаю: {target.Name}…");
                try
                {
                    var r = target.Clean(token);
                    deleted += r.Deleted;
                    skipped += r.Skipped;
                    if (r.Note != null) notes.Add($"{target.Name}: {r.Note}");
                    _log.LogInformation("Cleanup {Target}: freed {Bytes} bytes, {Deleted} deleted, {Skipped} skipped",
                        target.Id, r.FreedBytes, r.Deleted, r.Skipped);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Cleanup failed for {Target}", target.Id);
                    notes.Add($"{target.Name}: ошибка — {ex.Message}");
                }
            }

            long freed = Math.Max(0, GetSystemDrive().FreeBytes - freeBefore);
            return new CleanupSummary(freed, deleted, skipped, notes);
        }, token);
    }
}

public static class ByteSize
{
    /// <summary>1536 → "1,5 КБ"; 5_368_709_120 → "5,0 ГБ" (uses the current culture's decimal separator).</summary>
    public static string Format(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.0} {units[unit]}";
    }
}
