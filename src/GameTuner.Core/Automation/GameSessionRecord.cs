using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Automation;

/// <summary>Summary of one play session, shown in "Последние сессии".</summary>
public sealed record GameSessionRecord
{
    public string GameName { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public TimeSpan Duration { get; init; }

    public double? AverageFps { get; init; }
    public double? OnePercentLowFps { get; init; }

    public double? MaxCpuTempC { get; init; }
    public double? MaxGpuTempC { get; init; }
    public double AverageCpuLoad { get; init; }
    public double? AverageGpuLoad { get; init; }
}

public interface ISessionStore
{
    IReadOnlyList<GameSessionRecord> All { get; }
    void Add(GameSessionRecord record);
    event EventHandler? Changed;
}

/// <summary>Keeps the last <see cref="MaxRecords"/> sessions in a JSON file.</summary>
public sealed class SessionStore : ISessionStore
{
    public const int MaxRecords = 50;
    private readonly string _path;
    private readonly ILogger<SessionStore> _log;
    private readonly object _lock = new();
    private List<GameSessionRecord> _records;

    public SessionStore(ILogger<SessionStore>? log = null, string? path = null)
    {
        _log = log ?? NullLogger<SessionStore>.Instance;
        _path = path ?? AppPaths.SessionsFile;
        _records = Load();
    }

    public IReadOnlyList<GameSessionRecord> All
    {
        get { lock (_lock) return _records.ToList(); }
    }

    public event EventHandler? Changed;

    public void Add(GameSessionRecord record)
    {
        lock (_lock)
        {
            _records.Insert(0, record);   // newest first
            if (_records.Count > MaxRecords) _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_records));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to save sessions");
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<GameSessionRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<GameSessionRecord>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sessions file is corrupted, starting empty");
        }
        return new();
    }
}
