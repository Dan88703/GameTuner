using System.Diagnostics;
using GameTuner.Core.Backup;
using GameTuner.Core.Configs;
using GameTuner.Core.Performance;

namespace GameTuner.Core.Games;

/// <summary>One setting GameTuner wants to write, with a readable label for the UI.</summary>
public sealed record SettingChange(string Key, string Value, string Label);

/// <summary>What happened when a preset was applied.</summary>
public sealed class ApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int ChangedValues { get; init; }
    public int FilesChanged { get; init; }
    public string? BackupFolder { get; init; }
    public IReadOnlyList<string> MissingKeys { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Base class for a game GameTuner can configure.
///
/// The apply flow is identical for every game (template method pattern):
///   1. find the config file(s)
///   2. refuse if the game is running — games rewrite their config on exit,
///      which would silently undo our changes
///   3. back up all files
///   4. edit each file in memory (pure function, see <see cref="ConfigEditors"/>)
///   5. write it back with the original encoding
/// Subclasses only describe WHERE the files are and WHICH format/values to use.
/// </summary>
public abstract class GameProfile
{
    /// <summary>Stable id used for backups and settings (never shown to the user).</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    /// <summary>Executable names without ".exe"; used to detect the running game.</summary>
    public abstract IReadOnlyList<string> ProcessNames { get; }

    public abstract List<string> FindConfigFiles();

    public abstract List<SettingChange> GetSettings(PerformanceTier tier);

    /// <summary>Advice the user has to apply by hand (things that are not in the config file).</summary>
    public virtual string GetTips(PerformanceTier tier) => "";

    /// <summary>Edits the text of one config file.</summary>
    protected abstract ConfigEditResult Edit(string content, IReadOnlyDictionary<string, string> values);

    /// <summary>
    /// Used by the game watcher: is this process one of this game's processes?
    /// The window title is passed lazily because reading it is relatively slow.
    /// </summary>
    public virtual bool MatchesProcess(string processName, Func<string> windowTitle) =>
        ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public virtual bool IsRunning() =>
        ProcessNames.Any(name =>
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        });

    public ApplyResult Apply(PerformanceTier tier, IBackupService backups)
    {
        var files = FindConfigFiles();
        if (files.Count == 0)
            return Fail($"Не найден файл настроек {Name}. Запусти игру один раз и закрой её — она создаст файл.");

        if (IsRunning())
            return Fail($"{Name} сейчас запущена. Закрой игру — иначе при выходе она перезапишет настройки.");

        string backupFolder;
        try
        {
            backupFolder = backups.Backup(Id, files);
        }
        catch (Exception ex)
        {
            return Fail($"Не удалось сделать бэкап, ничего не изменено: {ex.Message}");
        }

        var values = GetSettings(tier).ToDictionary(s => s.Key, s => s.Value);
        var missing = new HashSet<string>();
        int changed = 0, filesChanged = 0;

        try
        {
            foreach (string file in files)
            {
                var (text, encoding) = TextFile.Read(file);
                var result = Edit(text, values);
                missing.UnionWith(result.MissingKeys);
                if (result.Changed == 0) continue;

                TextFile.Write(file, result.Content, encoding);
                changed += result.Changed;
                filesChanged++;
            }
        }
        catch (Exception ex)
        {
            // Something failed half-way: put every file back as it was.
            backups.Restore(backupFolder);
            return Fail($"Ошибка при изменении настроек, всё откатено: {ex.Message}");
        }

        return new ApplyResult
        {
            Success = true,
            ChangedValues = changed,
            FilesChanged = filesChanged,
            BackupFolder = backupFolder,
            MissingKeys = missing.OrderBy(k => k).ToList(),
            Message = changed == 0
                ? $"{Name}: настройки уже соответствуют пресету {tier}."
                : $"{Name}: пресет {tier} применён, изменено значений: {changed}."
        };
    }

    private static ApplyResult Fail(string message) => new() { Success = false, Message = message };

    /// <summary>Helper for subclasses: picks a value for the tier from a 4-element array.</summary>
    protected static string ByTier(PerformanceTier tier, string low, string medium, string high, string ultra) =>
        tier switch
        {
            PerformanceTier.Low => low,
            PerformanceTier.Medium => medium,
            PerformanceTier.High => high,
            _ => ultra
        };
}
