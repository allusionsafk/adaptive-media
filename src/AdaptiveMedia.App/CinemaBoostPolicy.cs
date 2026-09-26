namespace AdaptiveMedia;

/// <summary>Conservative, side-effect-free admission and restore rules for one panel.</summary>
public static class CinemaBoostPolicy
{
    private const uint InternalPanel = 0x80000000;

    public static bool CanStart(bool enabled, DisplayCapability? planned,
        IReadOnlyList<DisplayCapability> active, bool onAc, bool energySaver)
    {
        if (!enabled || !onAc || energySaver || planned is null ||
            planned.OutputTechnology != InternalPanel || planned.QualifiedWcgPeakNits is null ||
            string.IsNullOrWhiteSpace(planned.PnpId) ||
            planned.EdidFingerprint is not { Length: 64 } fingerprint ||
            !fingerprint.All(Uri.IsHexDigit)) return false;
        var matched = DisplayCapabilitySelection.ForAttempt(active, planned);
        return matched is { OutputTechnology: InternalPanel } && matched.QualifiedWcgPeakNits is not null;
    }

    public static bool ShouldRestore(int original, int boosted, int current, bool samePanel) =>
        samePanel && original is >= 0 and <= 100 && boosted is >= 0 and <= 100 &&
        original != boosted && current == boosted;
}


/// <summary>Brightness values read back by the independent watchdog, in percent.</summary>
public sealed record CinemaBoostEvidence(int OriginalBrightnessPercent, int ObservedBoostedPercent,
    string WmiInstanceName)
{
    public static CinemaBoostEvidence? TryParse(string? line)
    {
        if (line is null) return null;
        var parts = line.Split('|', 4);
        if (parts.Length != 4 || parts[0] != "BOOSTED" ||
            !int.TryParse(parts[1], out var original) || original is < 0 or >= 100 ||
            !int.TryParse(parts[2], out var observed) || observed != 100 ||
            !parts[3].StartsWith("DISPLAY\\", StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(parts[3], @"_[0-9]+$")) return null;
        return new(original, observed, parts[3]);
    }
}
