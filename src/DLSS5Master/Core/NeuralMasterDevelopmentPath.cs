namespace DLSS5Master.Core;

public sealed record NeuralMasterPathStage(string Number, string Title, string Status, string Description);

/// <summary>Visible integration status. Passing an isolated check never implies game readiness.</summary>
public static class NeuralMasterDevelopmentPath
{
    public const string Summary = "Native inputs → Layers before NR → per-pass neural rendering → Look → HDR transfer → Layers after NR → game output";
    public const string BuildStatus = "A separate D3D12 native-guide trial is available. It is not yet a general installer route. Broader game support, calibrated HDR/FG output and matched comparisons remain unfinished.";
    public static IReadOnlyList<NeuralMasterPathStage> Stages { get; } = new[]
    {
        new NeuralMasterPathStage("01", "Game depth & motion", "Native-guide trial",
            "The D3D12 trial uses the game's native DLSS colour, depth and motion inputs. Pre-Upscale, Post-Upscale and Auto work for admitted profiles. General discovery and per-game input conventions still need qualification."),
        new NeuralMasterPathStage("02", "Layers · Before NR", "Live controls",
            "Independent exposure, RGB balance, contrast, saturation and highlight controls can be assigned Before or After NR. Reported Onimusha trials found neutral identity and visible slider changes; those observations are not physical HDR calibration."),
        new NeuralMasterPathStage("03", "Per-pass neural rendering", "1–4 passes",
            "Each cascaded pass has its own feature/history and work scale. Old/New model selection and Normal/Lower/Lowest memory modes are implemented. Brief unenhanced transitions were reported with Lower/Lowest; the continuity correction still needs native and game qualification. General resize and recovery remain incomplete."),
        new NeuralMasterPathStage("04", "Look · detail & colour", "Implemented controls",
            "Strength blends the final edit. Local contrast, detail and skin detail use typed runtime controls; independent Refine processing is optional. Model Lab exposes only characterized intensity choices. Complete artistic parity is not established."),
        new NeuralMasterPathStage("05", "HDR transfer & white", "Component checks",
            "Scene-linear, scRGB and HDR10/PQ composition and scaled-pass colour reconstruction have isolated checks. Working white/peak values are processing anchors, not display calibration. Real and generated HDR frame brightness still needs controlled validation."),
        new NeuralMasterPathStage("06", "Layers · After NR", "Live controls",
            "After-NR colour Layers are composed after the neural cascade. Source alpha is preserved in component tests. Ordinary screenshots and diagnostic colour-data capture are available with explicit limits; lossless final HDR/FG capture remains unfinished."),
        new NeuralMasterPathStage("07", "Game output · SR / FG / HUD", "Limited game evidence",
            "Onimusha trial observations reported visible enhancement and stable marked shadows with the native-guide path. HUD isolation, unique generated frames, pacing, calibrated HDR and matched competitor quality/GPU-cost comparisons remain unverified.")
    };
}
