using System.Runtime.InteropServices;

namespace AdaptiveMedia;

/// <summary>External display or graphics failures that justify a single
/// conservative restart after the active player fails.</summary>
public enum PlaybackTransitionFailure { None, GraphicsDeviceLost, PowerTransition }

/// <param name="WallMilliseconds">Monotonic elapsed time including sleep.</param>
/// <param name="AwakeMilliseconds">Unbiased interrupt time excluding sleep.</param>
/// <param name="AcLine">Windows AC line status, if available.</param>
/// <param name="EnergySaver">Windows energy saver status, if available.</param>
public sealed record PlaybackPowerSnapshot(long WallMilliseconds, long AwakeMilliseconds,
    int? AcLine, int? EnergySaver);

public static class PlaybackTransitionRecovery
{
    private static readonly string[] DeviceLossSignatures =
    [
        "DXGI_ERROR_DEVICE_REMOVED", "DXGI_ERROR_DEVICE_RESET", "DXGI_ERROR_DEVICE_HUNG",
        "VK_ERROR_DEVICE_LOST", "0x887A0005", "0x887A0006", "0x887A0007",
    ];

    public static bool PowerChanged(PlaybackPowerSnapshot before, PlaybackPowerSnapshot after)
    {
        long wall = after.WallMilliseconds - before.WallMilliseconds;
        long awake = after.AwakeMilliseconds - before.AwakeMilliseconds;
        return wall >= 0 && awake >= 0 && wall - awake >= 2_000 ||
            before.AcLine is int firstAc && after.AcLine is int nextAc && firstAc != nextAc ||
            before.EnergySaver is int firstSaver && after.EnergySaver is int nextSaver && firstSaver != nextSaver;
    }

    public static PlaybackTransitionFailure Classify(string? playerError, bool powerTransitionObserved,
        bool adapterChanged = false)
    {
        if (powerTransitionObserved) return PlaybackTransitionFailure.PowerTransition;
        return adapterChanged || playerError is not null && DeviceLossSignatures.Any(signature =>
            playerError.Contains(signature, StringComparison.OrdinalIgnoreCase))
            ? PlaybackTransitionFailure.GraphicsDeviceLost : PlaybackTransitionFailure.None;
    }
}

