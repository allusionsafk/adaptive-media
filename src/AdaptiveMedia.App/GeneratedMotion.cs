using System.Globalization;

namespace AdaptiveMedia;

/// <summary>A validated, app-private mpv runtime whose FFmpeg carries the NVIDIA
/// Optical Flow FRUC filter. Experimental: never required, never used by Automatic.</summary>
public sealed record GeneratedMotionBackend(string Executable, string RuntimeId, string Description);

/// <summary>Power state that gates a heavy optional GPU path.</summary>
public sealed record GeneratedMotionPower(bool OnAc, bool EnergySaver);

/// <summary>Side-effect-free rules for when Generated Motion may be planned.</summary>
public static class GeneratedMotionPolicy
{
    public const string Renderer = "Generated Motion D3D11 (experimental)";
    public const string FilterLabel = "fruc";
    /// <summary>Measured real-time ceiling on the RTX 4080 Laptop GPU (1440p24 p95 37 ms against a 41.7 ms budget).</summary>
    public const long MaxPixels = 2560L * 1440;

    public static string? Unavailable(MediaInfo source, int itemCount, PlaybackCapabilities capabilities,
        GeneratedMotionBackend? backend, GeneratedMotionPower? power)
    {
        if (backend is null) return "the experimental Generated Motion runtime is not installed or did not validate";
        if (!capabilities.Nvidia || !capabilities.Rtx) return "an NVIDIA RTX GPU is required";
        if (power is not { OnAc: true, EnergySaver: false }) return "it needs AC power with Energy Saver off";
        if (itemCount != 1) return "it runs on one source at a time, not a playlist";
        if (!source.Known || source.Width <= 0 || source.Height <= 0) return "the source dimensions are unknown";
        if (source.IsHdr) return "the NVIDIA FRUC path is 8-bit SDR only; HDR, WCG and Dolby Vision keep their own paths";
        if (new[] { "p10", "p12", "p010", "p016", "10le", "12le" }.Any(x => source.PixelFormat.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return "the NVIDIA FRUC path is 8-bit only";
        if ((long)source.Width * source.Height > MaxPixels) return "sources above 1440p are not real-time on this GPU";
        if (TargetFps(source.Fps) is null) return "only 23.976–30 fps sources are supported";
        return null;
    }

    /// <summary>Doubles cinema and video rates; anything else is not attempted.</summary>
    public static double? TargetFps(double sourceFps) =>
        sourceFps is >= 23.9 and <= 30.05 ? Math.Round(sourceFps * 2, 3) : null;

    public static string FilterArgument(double targetFps) =>
        $"--vf=@{FilterLabel}:lavfi=[nvofruc=fps={targetFps.ToString("0.###", CultureInfo.InvariantCulture)}]";
}
