using System.Diagnostics;
using GameTuner.Core.Games;
using GameTuner.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Automation;

/// <summary>A running game the watcher found.</summary>
public sealed record DetectedGame(int ProcessId, string ProcessName, string DisplayName, string? ProfileId);

public interface IGameWatcher : IDisposable
{
    DetectedGame? Current { get; }
    event EventHandler<DetectedGame>? GameStarted;
    event EventHandler<DetectedGame>? GameStopped;
    void Start();
}

/// <summary>
/// Detects when a known game starts and stops.
///
/// Why polling every 3 seconds instead of WMI process-start events:
/// Win32_ProcessStartTrace needs admin rights, and the app runs as a normal
/// user by default. Listing processes every few seconds costs almost nothing
/// and a 3-second delay does not matter for a game that takes 20 s to load.
///
/// Only one game is tracked at a time — the first one found.
/// </summary>
public sealed class GameWatcher : IGameWatcher
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly IGameLibrary _library;
    private readonly ISettingsService _settings;
    private readonly ILogger<GameWatcher> _log;
    private Timer? _timer;
    private int _busy; // 0/1 guard: skip a tick if the previous one is still running

    public GameWatcher(IGameLibrary library, ISettingsService settings, ILogger<GameWatcher>? log = null)
    {
        _library = library;
        _settings = settings;
        _log = log ?? NullLogger<GameWatcher>.Instance;
    }

    public DetectedGame? Current { get; private set; }
    public event EventHandler<DetectedGame>? GameStarted;
    public event EventHandler<DetectedGame>? GameStopped;

    public void Start() => _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, Interval);

    private void Tick()
    {
        // Interlocked.Exchange returns the old value: if it was already 1,
        // another tick is still running and this one exits.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (Current != null)
            {
                if (!IsAlive(Current.ProcessId))
                {
                    var stopped = Current;
                    Current = null;
                    _log.LogInformation("Game stopped: {Game}", stopped.DisplayName);
                    GameStopped?.Invoke(this, stopped);
                }
                return;
            }

            var found = FindRunningGame();
            if (found != null)
            {
                Current = found;
                _log.LogInformation("Game started: {Game} (PID {Pid})", found.DisplayName, found.ProcessId);
                GameStarted?.Invoke(this, found);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Game watcher tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private DetectedGame? FindRunningGame()
    {
        var extra = new HashSet<string>(
            _settings.Current.ExtraGameExecutables.Select(e => e.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        var processes = Process.GetProcesses();
        try
        {
            foreach (var p in processes)
            {
                string name = p.ProcessName;
                string Title() { try { return p.MainWindowTitle; } catch { return ""; } }

                var profile = _library.Profiles.FirstOrDefault(g => g.MatchesProcess(name, Title));
                if (profile != null) return new DetectedGame(p.Id, name, profile.Name, profile.Id);

                if (extra.Contains(name)) return new DetectedGame(p.Id, name, name, null);
            }
            return null;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }      // no such process
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // exists, but access denied
    }

    public void Dispose() => _timer?.Dispose();
}
