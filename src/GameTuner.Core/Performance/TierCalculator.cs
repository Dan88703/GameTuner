using System.Text.RegularExpressions;
using GameTuner.Core.Hardware;

namespace GameTuner.Core.Performance;

/// <summary>
/// Turns raw hardware info into a single PerformanceTier.
///
/// How it works: each component gets a score from 0 to 100, then a weighted
/// sum is taken (GPU matters most for games). The scores are heuristics
/// based on the GPU/CPU model names; they are not a benchmark, but they are
/// good enough to choose between Low / Medium / High / Ultra presets.
/// </summary>
public interface ITierCalculator
{
    TierResult Calculate(SystemInfo info);
}

public sealed class TierCalculator : ITierCalculator
{
    private const double GpuWeight = 0.60;
    private const double CpuWeight = 0.25;
    private const double RamWeight = 0.15;

    public TierResult Calculate(SystemInfo info)
    {
        var result = new TierResult
        {
            GpuScore = ScoreGpu(info.PrimaryGpu),
            CpuScore = ScoreCpu(info),
            RamScore = ScoreRam(info.RamGb)
        };

        double total = result.GpuScore * GpuWeight
                     + result.CpuScore * CpuWeight
                     + result.RamScore * RamWeight;

        result.Tier = total switch
        {
            < 30 => PerformanceTier.Low,
            < 55 => PerformanceTier.Medium,
            < 78 => PerformanceTier.High,
            _ => PerformanceTier.Ultra
        };

        // A weak link limits everything else, whatever the total says.
        if (info.RamGb > 0 && info.RamGb < 7 && result.Tier > PerformanceTier.Low)
        {
            result.Tier = PerformanceTier.Low;
            result.Notes.Add("Меньше 8 ГБ ОЗУ — это главное узкое место, пресет понижен до Low.");
        }
        else if (info.RamGb > 0 && info.RamGb < 15 && result.Tier > PerformanceTier.High)
        {
            result.Tier = PerformanceTier.High;
            result.Notes.Add("Меньше 16 ГБ ОЗУ — Ultra может вызывать фризы, выбран High.");
        }

        if (info.PrimaryGpu is { IsIntegrated: true } && result.Tier > PerformanceTier.Medium)
        {
            result.Tier = PerformanceTier.Medium;
            result.Notes.Add("Используется встроенная графика — выше Medium подниматься не стоит.");
        }

        if (info.SystemDriveIsSsd == false)
            result.Notes.Add("Система стоит на HDD — возможны подгрузки и статтеры. SSD сильно поможет.");

        if (info.Gpus.Count > 1 && info.Gpus.Any(g => !g.IsIntegrated))
            result.Notes.Add("В ноутбуке две видеокарты. Проверь в «Параметры → Дисплей → Графика», что игры запускаются на дискретной.");

        if (info.HasBattery)
            result.Notes.Add("Играй с подключённой зарядкой: от батареи ноутбук сильно режет частоты CPU/GPU.");

        return result;
    }

    // ---------------- GPU ----------------

    /// <summary>
    /// Parses the GPU model name and maps it to a score.
    /// Example: "NVIDIA GeForce RTX 4060 Laptop GPU" -> family RTX, number 4060
    /// -> generation 40, class 60 -> base score for class 60 adjusted by generation.
    /// </summary>
    public static int ScoreGpu(GpuInfo? gpu)
    {
        if (gpu == null) return 10;
        string n = gpu.Name.ToLowerInvariant();

        // --- NVIDIA ---
        var nv = Regex.Match(n, @"\b(rtx|gtx|mx)\s*(\d{3,4})");
        if (nv.Success)
        {
            string family = nv.Groups[1].Value;
            int number = int.Parse(nv.Groups[2].Value);

            if (family == "mx") return 15;

            int generation = number / 100;   // 4060 -> 40, 1650 -> 16
            int modelClass = number % 100;   // 4060 -> 60, 1650 -> 50

            if (family == "gtx")
            {
                return generation switch
                {
                    16 => modelClass >= 60 ? 42 : 35,
                    10 => modelClass switch { >= 80 => 55, >= 70 => 48, >= 60 => 38, _ => 25 },
                    _ => 18 // GTX 9xx and older
                };
            }

            // RTX
            int rtxBase = modelClass switch
            {
                >= 90 => 95,
                >= 80 => 88,
                >= 70 => 78,
                >= 60 => 65,
                _ => 50 // 50 class (RTX 3050, 4050, 2050)
            };
            int genAdjust = generation switch
            {
                >= 50 => 5,
                40 => 0,
                30 => -8,
                _ => -15 // RTX 20
            };
            return Math.Clamp(rtxBase + genAdjust, 0, 100);
        }

        // --- AMD discrete (RX 6600M, RX 7600S, RX 9070 ...) ---
        var amd = Regex.Match(n, @"\brx\s*(\d)(\d)\d{2}");
        if (amd.Success)
        {
            int generation = amd.Groups[1].Value[0] - '0'; // 7 in 7600
            int modelClass = amd.Groups[2].Value[0] - '0'; // 6 in 7600
            int amdBase = modelClass switch
            {
                >= 9 => 92, 8 => 84, 7 => 72, 6 => 58, _ => 40
            };
            int genAdjust = generation switch
            {
                9 => 5, 7 => 0, 6 => -6, _ => -14
            };
            return Math.Clamp(amdBase + genAdjust, 0, 100);
        }

        // --- Integrated graphics ---
        if (gpu.IsIntegrated)
        {
            if (Regex.IsMatch(n, @"8[89]0m")) return 32;   // Radeon 880M / 890M
            if (n.Contains("780m")) return 30;
            if (n.Contains("760m") || n.Contains("680m")) return 26;
            if (n.Contains("arc")) return 25;              // Intel Arc (Meteor/Lunar Lake iGPU)
            if (n.Contains("iris")) return 15;             // Iris Xe
            if (n.Contains("vega")) return 12;
            if (n.Contains("radeon")) return 15;
            return 8;                                      // Intel UHD / HD
        }

        // --- Intel Arc discrete ---
        if (n.Contains("arc")) return 45;

        // --- Unknown discrete GPU: guess by VRAM ---
        return gpu.VramGb switch
        {
            >= 12 => 80,
            >= 8 => 65,
            >= 6 => 50,
            >= 4 => 35,
            _ => 20
        };
    }

    // ---------------- CPU ----------------

    public static int ScoreCpu(SystemInfo info)
    {
        int score = info.CpuThreads switch
        {
            >= 16 => 90,
            >= 12 => 75,
            >= 8 => 60,
            >= 4 => 35,
            _ => 15
        };

        // Low-power laptop chips ("i5-1235U", "Ryzen 5 7520U") have a small
        // power limit and throttle quickly under gaming load.
        if (Regex.IsMatch(info.CpuName, @"\d{4,5}U\b", RegexOptions.IgnoreCase))
            score -= 15;

        return Math.Clamp(score, 0, 100);
    }

    // ---------------- RAM ----------------

    public static int ScoreRam(double ramGb) => ramGb switch
    {
        >= 31 => 100, // 32 GB is reported as ~31.x
        >= 15 => 80,
        >= 11 => 60,
        >= 7 => 40,
        _ => 15
    };
}
