using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace GameTuner.Core.Tuning;

/// <summary>
/// Admin rights on request: the app starts as a normal user and only asks
/// for elevation (UAC prompt) when the user wants admin-only features
/// (temperatures, FPS counter).
/// </summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Starts a new elevated copy of the current exe. Returns false if the
    /// user clicked "No" in the UAC dialog. The caller must exit on success.
    /// </summary>
    public static bool RestartElevated(string arguments = "")
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;

        try
        {
            // Verb "runas" + UseShellExecute is what triggers the UAC prompt.
            Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }
}
