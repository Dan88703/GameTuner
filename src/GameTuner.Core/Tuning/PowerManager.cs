using System.Diagnostics;
using System.Text.RegularExpressions;
using GameTuner.Core.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTuner.Core.Tuning;

public interface IPowerManager
{
    /// <summary>Switches to maximum performance. Returns a message for the user.</summary>
    string EnableMaxPerformance();

    /// <summary>Restores the power plan saved by <see cref="EnableMaxPerformance"/>.</summary>
    string Restore();

    /// <summary>True while a saved state exists (i.e. max performance is "on" by us).</summary>
    bool IsBoosted { get; }

    string GetActivePlanName();
}

/// <summary>
/// Power plan / power mode control through powercfg.exe and powrprof.dll.
/// No admin rights needed: users may switch their own power plan.
///
/// Many modern laptops (Modern Standby) hide the classic "High performance"
/// plan and only show the "Power mode" slider, so we try, in order:
///   1. classic High performance plan
///   2. Ultimate performance plan
///   3. the "Power mode" overlay set to "Best performance"
/// Before the first change, the current plan AND overlay are saved to a file,
/// so the state survives an app crash or reboot and can still be restored.
/// </summary>
public sealed class PowerManager : IPowerManager
{
    private const string HighPerformancePlan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string UltimatePlan = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private static readonly Regex GuidPattern =
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private readonly ILogger<PowerManager> _log;
    private readonly string _stateFile;

    public PowerManager(ILogger<PowerManager>? log = null, string? stateFile = null)
    {
        _log = log ?? NullLogger<PowerManager>.Instance;
        _stateFile = stateFile ?? AppPaths.PowerStateFile;
    }

    public bool IsBoosted => File.Exists(_stateFile);

    public string EnableMaxPerformance()
    {
        SaveStateOnce();

        if (RunPowercfg($"/setactive {HighPerformancePlan}", out _))
            return Log("Включена схема питания «Высокая производительность».");
        if (RunPowercfg($"/setactive {UltimatePlan}", out _))
            return Log("Включена схема питания «Максимальная производительность».");

        try
        {
            if (Native.PowerSetActiveOverlayScheme(OverlayBestPerformance) == 0)
                return Log("Режим питания Windows: «Максимальная производительность».");
        }
        catch (Exception ex) { _log.LogWarning(ex, "Power overlay API is not available"); }

        return Log("Не удалось сменить режим питания. Сделай это вручную: Параметры → Система → Питание.");
    }

    public string Restore()
    {
        if (!File.Exists(_stateFile)) return "Схема питания не менялась — возвращать нечего.";

        string[] lines = File.ReadAllLines(_stateFile);
        bool ok = true;

        if (lines.Length > 0 && GuidPattern.IsMatch(lines[0]))
            ok &= RunPowercfg($"/setactive {lines[0].Trim()}", out _);

        if (lines.Length > 1 && Guid.TryParse(lines[1], out Guid overlay))
        {
            try { Native.PowerSetActiveOverlayScheme(overlay); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not restore power overlay"); }
        }

        if (!ok) return Log("Не удалось вернуть схему питания.");
        File.Delete(_stateFile);
        return Log("Питание возвращено к исходному.");
    }

    public string GetActivePlanName()
    {
        // Output (localized): "Power Scheme GUID: 381b...  (Balanced)"
        if (!RunPowercfg("/getactivescheme", out string output)) return "неизвестно";
        int open = output.LastIndexOf('(');
        int close = output.LastIndexOf(')');
        return open >= 0 && close > open ? output[(open + 1)..close] : output.Trim();
    }

    /// <summary>
    /// Saves the current state only if nothing is saved yet. If the user
    /// presses "boost" twice, the second call must NOT overwrite the original
    /// plan with the already-boosted one.
    /// </summary>
    private void SaveStateOnce()
    {
        if (File.Exists(_stateFile)) return;

        string plan = RunPowercfg("/getactivescheme", out string output) && GuidPattern.Match(output) is { Success: true } m
            ? m.Value
            : "";

        string overlay = "";
        try
        {
            if (Native.PowerGetEffectiveOverlayScheme(out Guid g) == 0) overlay = g.ToString();
        }
        catch (Exception) { /* old Windows */ }

        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllLines(_stateFile, new[] { plan, overlay });
    }

    private static bool RunPowercfg(string args, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null) return false;
            output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string Log(string message)
    {
        _log.LogInformation("{Message}", message);
        return message;
    }
}
