using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GameTuner.Core.Configs;

/// <summary>Result of editing a config file's text in memory.</summary>
public sealed record ConfigEditResult(string Content, int Changed, IReadOnlyList<string> MissingKeys);

/// <summary>
/// Pure text editors for the config formats games use.
///
/// Design rule shared by all editors: they ONLY change keys that already exist.
/// Adding keys a game does not know (or renamed in an update) could break its
/// config, so unknown keys are reported as "missing" and left alone.
///
/// The editors work on strings, not files, so they can be unit-tested
/// without touching the disk.
/// </summary>
public static class ConfigEditors
{
    // ------------------------------------------------------------------
    // Valve KeyValues (CS2 cs2_video.txt):    "setting.msaa_samples"		"4"
    // ------------------------------------------------------------------

    private static readonly Regex ValveLine =
        new(@"^(\s*)""([^""]+)""(\s+)""([^""]*)""(.*)$", RegexOptions.Compiled);

    public static ConfigEditResult EditValveKeyValues(string content, IReadOnlyDictionary<string, string> values)
    {
        var wanted = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int changed = 0;

        string result = EditLines(content, line =>
        {
            var m = ValveLine.Match(line);
            if (!m.Success) return line;

            string key = m.Groups[2].Value;
            if (!wanted.TryGetValue(key, out string? newValue)) return line;

            found.Add(key);
            if (m.Groups[4].Value == newValue) return line;

            changed++;
            // Keep the original indentation, spacing and trailing comment.
            return $"{m.Groups[1].Value}\"{key}\"{m.Groups[3].Value}\"{newValue}\"{m.Groups[5].Value}";
        });

        return new ConfigEditResult(result, changed, Missing(wanted.Keys, found, null));
    }

    // ------------------------------------------------------------------
    // "key:value" per line (Minecraft options.txt)
    // ------------------------------------------------------------------

    /// <param name="alternativeKeys">
    /// Keys that exist only in some versions of the game (e.g. Minecraft's
    /// old "fancyGraphics" vs new "graphicsMode"). They are not reported as missing.
    /// </param>
    public static ConfigEditResult EditColonOptions(string content, IReadOnlyDictionary<string, string> values,
                                                    IReadOnlySet<string>? alternativeKeys = null)
    {
        var found = new HashSet<string>();
        int changed = 0;

        string result = EditLines(content, line =>
        {
            // Split on the FIRST colon only: "key_key.attack:key.mouse.left" has two.
            int colon = line.IndexOf(':');
            if (colon <= 0) return line;

            string key = line[..colon];
            string oldValue = line[(colon + 1)..];
            if (!values.TryGetValue(key, out string? newValue)) return line;

            found.Add(key);
            newValue = MatchQuotes(oldValue, newValue);
            if (oldValue == newValue) return line;

            changed++;
            return $"{key}:{newValue}";
        });

        return new ConfigEditResult(result, changed, Missing(values.Keys, found, alternativeKeys));
    }

    // ------------------------------------------------------------------
    // XML elements with a value attribute (GTA V settings.xml):  <MSAA value="0" />
    // ------------------------------------------------------------------

    public static ConfigEditResult EditXmlValueAttributes(string content, IReadOnlyDictionary<string, string> values)
    {
        var doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        var missing = new List<string>();
        int changed = 0;

        foreach (var (key, newValue) in values)
        {
            // The same element name can appear in several sections: change all of them.
            var elements = doc.Descendants(key).ToList();
            if (elements.Count == 0)
            {
                missing.Add(key);
                continue;
            }

            foreach (var attr in elements.Select(e => e.Attribute("value")).OfType<XAttribute>())
            {
                if (attr.Value == newValue) continue;
                attr.Value = newValue;
                changed++;
            }
        }

        // XDocument.ToString() drops the <?xml ...?> declaration, so add it back.
        string body = doc.ToString(SaveOptions.DisableFormatting);
        string output = doc.Declaration != null ? doc.Declaration + body : body;
        return new ConfigEditResult(output, changed, missing);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies <paramref name="edit"/> to every line while keeping the file's
    /// original line endings (CRLF or LF) — games and diff tools both care.
    /// </summary>
    private static string EditLines(string content, Func<string, string> edit)
    {
        var sb = new StringBuilder(content.Length + 64);
        string[] parts = content.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            string line = parts[i];
            bool hadCr = line.EndsWith('\r');
            if (hadCr) line = line[..^1];

            sb.Append(edit(line));
            if (hadCr) sb.Append('\r');
            if (i < parts.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Keeps the quoting style of the original value: if the game wrote
    /// "fast" with quotes, the new value is written with quotes too.
    /// </summary>
    internal static string MatchQuotes(string oldValue, string newValue)
    {
        bool quoted = oldValue.Length >= 2 && oldValue.StartsWith('"') && oldValue.EndsWith('"');
        string bare = newValue.Trim('"');
        return quoted ? $"\"{bare}\"" : bare;
    }

    private static List<string> Missing(IEnumerable<string> wanted, HashSet<string> found, IReadOnlySet<string>? ignore) =>
        wanted.Where(k => !found.Contains(k) && (ignore == null || !ignore.Contains(k))).ToList();
}

/// <summary>Reads and writes text files without changing their encoding (BOM or no BOM).</summary>
public static class TextFile
{
    public static (string Text, Encoding Encoding) Read(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        return (text, reader.CurrentEncoding);
    }

    public static void Write(string path, string text, Encoding encoding)
    {
        // Write to a temp file first, then swap: if the PC crashes mid-write,
        // the original config is not left half-written.
        string temp = path + ".gametuner.tmp";
        File.WriteAllText(temp, text, encoding);
        File.Move(temp, path, overwrite: true);
    }
}
