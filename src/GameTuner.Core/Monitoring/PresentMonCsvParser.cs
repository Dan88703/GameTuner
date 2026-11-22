using System.Globalization;

namespace GameTuner.Core.Monitoring;

/// <summary>
/// Parses PresentMon's CSV output line by line.
///
/// The first line is a header; the frame time column has different names in
/// different PresentMon versions:
///   1.x and 2.x with --v1_metrics : "MsBetweenPresents"
///   2.x default                    : "FrameTime"
/// So the parser finds the column by name instead of using a fixed index.
/// </summary>
public sealed class PresentMonCsvParser
{
    private static readonly string[] FrameTimeColumns = { "MsBetweenPresents", "FrameTime" };
    private int _column = -1;

    public bool HasHeader => _column >= 0;

    /// <summary>Returns true and the frame time when the line is a data row.</summary>
    public bool TryParse(string? line, out double frameTimeMs)
    {
        frameTimeMs = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;

        string[] cells = line.Split(',');

        if (_column < 0)
        {
            // Still waiting for the header line (PresentMon may print notices before it).
            foreach (string name in FrameTimeColumns)
            {
                int index = Array.FindIndex(cells, c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    _column = index;
                    break;
                }
            }
            return false;
        }

        if (_column >= cells.Length) return false;
        // PresentMon always uses '.' as the decimal separator, regardless of Windows locale.
        return double.TryParse(cells[_column], NumberStyles.Float, CultureInfo.InvariantCulture, out frameTimeMs);
    }
}
