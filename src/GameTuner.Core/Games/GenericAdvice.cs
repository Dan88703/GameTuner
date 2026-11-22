using GameTuner.Core.Hardware;
using GameTuner.Core.Performance;

namespace GameTuner.Core.Games;

/// <summary>
/// Universal recommendations for any game that GameTuner cannot configure
/// directly: what preset, resolution, upscaler and FPS limit to pick in the
/// game's own menu.
/// </summary>
public static class GenericAdvice
{
    public static List<string> Build(SystemInfo info, TierResult tier)
    {
        var advice = new List<string>();
        var gpu = info.PrimaryGpu;
        string gpuName = gpu?.Name.ToLowerInvariant() ?? "";

        advice.Add($"Общий пресет графики: {TierName(tier.Tier)}.");

        // Resolution: on weak hardware, 1080p matters more than any setting.
        if (info.ScreenWidth > 1920 && tier.Tier <= PerformanceTier.Medium)
            advice.Add($"Экран {info.ScreenWidth}×{info.ScreenHeight}: для стабильности играй в 1920×1080 " +
                       "или включи апскейлер — нативное разрешение будет тяжёлым.");
        else if (info.ScreenWidth > 0)
            advice.Add($"Разрешение: нативное {info.ScreenWidth}×{info.ScreenHeight}.");

        // Upscaler: DLSS needs an RTX card, FSR / XeSS work everywhere.
        if (gpuName.Contains("rtx"))
            advice.Add("Апскейлер: DLSS (режим «Качество»), если игра его поддерживает.");
        else if (gpuName.Contains("arc"))
            advice.Add("Апскейлер: XeSS или FSR в режиме «Качество».");
        else
            advice.Add("Апскейлер: FSR в режиме «Качество» (или «Баланс» на Low).");

        // Stable frame time is about a cap, not about maximum FPS.
        advice.Add(tier.Tier switch
        {
            PerformanceTier.Low => "Ограничь FPS на 30–45 в одиночных играх — это убирает рывки и нагрев.",
            PerformanceTier.Medium => "Ограничь FPS на 60: ровные 60 ощущаются лучше, чем скачущие 50–80.",
            _ => info.RefreshRateHz > 60
                ? $"Ограничь FPS чуть ниже частоты экрана: {info.RefreshRateHz - 3} для {info.RefreshRateHz} Гц — меньше задержка и нагрев."
                : "Ограничь FPS на 60 — у экрана 60 Гц, больше он всё равно не покажет."
        });

        advice.Add("Что снижать в первую очередь, если лагает: тени → объёмные эффекты/туман → отражения → трава/растительность → сглаживание.");
        advice.Add("Текстуры можно держать высокими, пока хватает видеопамяти" +
                   (gpu is { VramGb: > 0 } ? $" (у тебя {gpu.VramGb} ГБ)." : "."));

        advice.AddRange(tier.Notes);
        return advice;
    }

    public static string TierName(PerformanceTier tier) => tier switch
    {
        PerformanceTier.Low => "Низкий (Low)",
        PerformanceTier.Medium => "Средний (Medium)",
        PerformanceTier.High => "Высокий (High)",
        _ => "Ультра (Ultra)"
    };
}
