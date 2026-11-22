using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using GameTuner.Core.Tuning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Monitoring;

public interface IFpsMonitor : IDisposable
{
    /// <summary>PresentMon.exe is downloaded.</summary>
    bool IsInstalled { get; }

    /// <summary>Installed AND we have admin rights (PresentMon needs ETW access).</summary>
    bool CanCapture { get; }

    bool IsCapturing { get; }

    /// <summary>Downloads the latest PresentMon from its official GitHub releases.</summary>
    Task<bool> InstallAsync(CancellationToken token = default);

    void Start(int processId);

    /// <summary>Stops capturing and returns the whole-session statistics.</summary>
    FrameStats? Stop();

    /// <summary>FPS over the last 2 seconds, or null if not capturing.</summary>
    FrameStats? Live { get; }

    /// <summary>Human-readable capture state, shown when no FPS is available.</summary>
    string Status { get; }
}

/// <summary>
/// FPS capture through Intel's PresentMon (MIT licensed, github.com/GameTechDev/PresentMon).
///
/// How it works: PresentMon listens to Windows ETW events that the graphics
/// stack emits on every frame "present". We start it as a child process for
/// one game PID with CSV output to stdout, read that stdout line by line on a
/// background task, and feed frame times into a <see cref="FrameRecorder"/>.
///
/// It is NOT bundled with GameTuner: on first use the user confirms and we
/// download the official exe into %LOCALAPPDATA%\GameTuner\tools.
/// </summary>
public sealed class PresentMonService : IFpsMonitor
{
    private const string ReleasesApi = "https://api.github.com/repos/GameTechDev/PresentMon/releases/latest";
    private const string SessionName = "GameTunerCapture";

    private readonly ILogger<PresentMonService> _log;
    private readonly FrameRecorder _frames = new();
    private Process? _process;
    private volatile string _status = "Не запущен";
    private volatile string? _lastError;
    private int _linesReceived;
    private bool _headerFound;

    public PresentMonService(ILogger<PresentMonService>? log = null)
    {
        _log = log ?? NullLogger<PresentMonService>.Instance;
    }

    public static string ExePath => Path.Combine(AppPaths.ToolsFolder, "PresentMon.exe");

    public bool IsInstalled => File.Exists(ExePath);
    public bool CanCapture => IsInstalled && Elevation.IsElevated;
    public bool IsCapturing => _process is { HasExited: false };
    public FrameStats? Live => IsCapturing ? _frames.Live() : null;
    public string Status => _status;

    public async Task<bool> InstallAsync(CancellationToken token = default)
    {
        try
        {
            using var http = new HttpClient();
            // GitHub's API rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GameTuner", "1.0"));

            await using var json = await http.GetStreamAsync(ReleasesApi, token);
            using var doc = await JsonDocument.ParseAsync(json, cancellationToken: token);

            // The release has several assets (installer .msi, console .exe, sources).
            // We need the x64 console exe, e.g. "PresentMon-2.3.0-x64.exe".
            string? url = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => (Name: a.GetProperty("name").GetString() ?? "",
                              Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .Where(a => a.Name.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase) &&
                            a.Name.EndsWith("x64.exe", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Url)
                .FirstOrDefault();

            if (url == null)
            {
                _log.LogWarning("No x64 exe found in the latest PresentMon release");
                return false;
            }

            Directory.CreateDirectory(AppPaths.ToolsFolder);
            string temp = ExePath + ".download";
            await using (var source = await http.GetStreamAsync(url, token))
            await using (var target = File.Create(temp))
            {
                await source.CopyToAsync(target, token);
            }
            File.Move(temp, ExePath, overwrite: true);

            _log.LogInformation("PresentMon downloaded from {Url}", url);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "PresentMon download failed");
            return false;
        }
    }

    public void Start(int processId)
    {
        Stop();
        if (!CanCapture)
        {
            _status = !IsInstalled ? "PresentMon не установлен" : "Нужен режим администратора";
            return;
        }

        _frames.Reset();
        var parser = new PresentMonCsvParser();

        var psi = new ProcessStartInfo(ExePath)
        {
            // --output_stdout:          CSV rows go to our redirected stdout.
            // --no_console_stats:       no live console table — it needs a real console
            //                           window, and we run PresentMon hidden.
            // --stop_existing_session:  clean up a session left over from a crash.
            // --terminate_on_proc_exit: PresentMon exits by itself when the game closes.
            Arguments = $"--process_id {processId} --output_stdout --no_console_stats " +
                        $"--session_name {SessionName} --stop_existing_session --terminate_on_proc_exit",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            // OutputDataReceived fires on a thread-pool thread for each line of stdout.
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                int n = Interlocked.Increment(ref _linesReceived);
                // The first lines tell us what PresentMon actually prints (header or an error).
                if (n <= 3) _log.LogInformation("PresentMon stdout: {Line}", e.Data);

                if (parser.TryParse(e.Data, out double ms))
                {
                    _frames.Add(ms);
                    _status = "Запись FPS";
                }
                else if (!_headerFound && parser.HasHeader)
                {
                    _headerFound = true;
                    _status = "Подключено, ждём кадры";
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                _lastError = e.Data.Trim();
                _log.LogWarning("PresentMon stderr: {Line}", e.Data);
            };
            // Exited tells us when PresentMon dies — before, this was silent and the UI
            // kept saying "waiting for frames" forever.
            process.Exited += (_, _) =>
            {
                int code = SafeExitCode(process);
                _log.LogWarning("PresentMon exited with code {Code} after {Lines} stdout lines", code, _linesReceived);
                _status = _lastError != null
                    ? $"PresentMon остановился (код {code}): {_lastError}"
                    : $"PresentMon остановился (код {code})";
            };

            _linesReceived = 0;
            _headerFound = false;
            _lastError = null;
            _status = "Запуск PresentMon…";
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _log.LogInformation("PresentMon capture started for PID {Pid}", processId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start PresentMon");
            _status = $"Не удалось запустить PresentMon: {ex.Message}";
            _process = null;
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch { return -1; }
    }

    public FrameStats? Stop()
    {
        if (_process == null) return null;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { _log.LogDebug(ex, "PresentMon kill failed"); }
        finally
        {
            _process.Dispose();
            _process = null;
            _status = "Не запущен";
        }
        return _frames.Session();
    }

    public void Dispose() => Stop();
}
