using System.Text.Json;

namespace GameTuner.Core.Backup;

public interface IBackupService
{
    /// <summary>Copies all files into a new timestamped folder; returns that folder.</summary>
    string Backup(string gameId, IEnumerable<string> files);

    /// <summary>Copies every file of a backup folder back to its original location.</summary>
    int Restore(string backupFolder);

    /// <summary>Restores the newest backup of a game. Returns the number of files restored.</summary>
    int RestoreLatest(string gameId);

    /// <summary>Backup folders of a game, newest first.</summary>
    IReadOnlyList<string> ListBackups(string gameId);
}

/// <summary>
/// Copies config files before GameTuner touches them, so every change can be undone.
///
///   &lt;root&gt;\&lt;gameId&gt;\&lt;yyyyMMdd_HHmmss_fff&gt;\
///       file0_options.txt
///       manifest.json     { "file0_options.txt": "C:\\Users\\...\\options.txt" }
///
/// The manifest remembers the original path of each copy, so restore works even
/// when several files share the same name (e.g. one cs2_video.txt per Steam account).
/// Only the newest <see cref="MaxBackupsPerGame"/> backups are kept.
/// </summary>
public sealed class BackupService : IBackupService
{
    public const int MaxBackupsPerGame = 20;
    private const string ManifestName = "manifest.json";

    private readonly string _root;

    public BackupService(string? root = null)
    {
        _root = root ?? Path.Combine(AppPaths.DataFolder, "Backups");
    }

    public string Backup(string gameId, IEnumerable<string> files)
    {
        // Milliseconds in the name: two backups within one second must not collide.
        string folder = Path.Combine(GameFolder(gameId), DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(folder);

        var manifest = new Dictionary<string, string>();
        int index = 0;
        foreach (string file in files)
        {
            string copyName = $"file{index++}_{Path.GetFileName(file)}";
            File.Copy(file, Path.Combine(folder, copyName), overwrite: true);
            manifest[copyName] = file;
        }

        File.WriteAllText(Path.Combine(folder, ManifestName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        PruneOldBackups(gameId);
        return folder;
    }

    public int Restore(string backupFolder)
    {
        string manifestPath = Path.Combine(backupFolder, ManifestName);
        if (!File.Exists(manifestPath)) return 0;

        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath)) ?? new();
        int restored = 0;
        foreach (var (copyName, originalPath) in manifest)
        {
            string copy = Path.Combine(backupFolder, copyName);
            if (!File.Exists(copy)) continue;
            File.Copy(copy, originalPath, overwrite: true);
            restored++;
        }
        return restored;
    }

    public int RestoreLatest(string gameId)
    {
        string? latest = ListBackups(gameId).FirstOrDefault();
        return latest == null ? 0 : Restore(latest);
    }

    public IReadOnlyList<string> ListBackups(string gameId)
    {
        string dir = GameFolder(gameId);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        // Folder names are sortable timestamps, so descending string order = newest first.
        return Directory.GetDirectories(dir).OrderByDescending(d => d, StringComparer.Ordinal).ToList();
    }

    private void PruneOldBackups(string gameId)
    {
        foreach (string old in ListBackups(gameId).Skip(MaxBackupsPerGame))
        {
            try { Directory.Delete(old, recursive: true); }
            catch (IOException) { /* in use — try next time */ }
        }
    }

    private string GameFolder(string gameId) =>
        Path.Combine(_root, string.Concat(gameId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
}
