using System.Runtime.InteropServices;

namespace AdaptiveMedia;

/// <summary>Captures power state before a player starts and compares it after
/// failure. The active display identity is re-probed only on a failed attempt.</summary>
public sealed class PlaybackTransitionObserver
{
    private readonly DisplayCapability? _plannedDisplay;
    private readonly Func<PlaybackPowerSnapshot?> _capturePower;
    private readonly PlaybackPowerSnapshot? _before;

    public PlaybackTransitionObserver(DisplayCapability? plannedDisplay,
        Func<PlaybackPowerSnapshot?>? powerSource = null)
    {
        _plannedDisplay = plannedDisplay;
        _capturePower = powerSource ?? CapturePower;
        _before = _capturePower();
    }

    public bool ObservePowerTransition() =>
        _before is { } before && _capturePower() is { } after && PlaybackTransitionRecovery.PowerChanged(before, after);

    public PlaybackTransitionFailure ClassifyFailure(string? playerError)
    {
        bool power = ObservePowerTransition();
        bool displayChanged = _plannedDisplay is not null &&
            DisplayCapabilitySelection.ForAttempt(DisplayCapabilityProbe.Read(), _plannedDisplay) is null;
        return PlaybackTransitionRecovery.Classify(playerError, power, displayChanged);
    }

    private static PlaybackPowerSnapshot? CapturePower()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!QueryUnbiasedInterruptTime(out ulong awake)) return null;
            int? ac = null, saver = null;
            if (GetSystemPowerStatus(out var status))
            {
                if (status.AcLineStatus is 0 or 1) ac = status.AcLineStatus;
                if (status.SystemStatusFlag is 0 or 1) saver = status.SystemStatusFlag;
            }
            return new(Environment.TickCount64, (long)(awake / 10_000), ac, saver);
        }
        catch (EntryPointNotFoundException) { return null; }
        catch (DllNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
