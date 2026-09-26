namespace AdaptiveMedia;

public sealed record AdvancedColorFlags(bool AdvancedColorSupported, bool AdvancedColorActive,
    bool LimitedByPolicy, bool HdrSupported, bool HdrUserEnabled, bool HdrActive,
    bool WcgSupported, bool WcgUserEnabled, bool WcgActive, string Mode)
{
    public static AdvancedColorFlags Parse(uint flags, uint activeMode)
    {
        bool hdrUser = (flags & 0x20) != 0;
        bool wcgUser = (flags & 0x80) != 0;
        return new((flags & 0x1) != 0, (flags & 0x2) != 0, (flags & 0x8) != 0,
            (flags & 0x10) != 0, hdrUser, activeMode == 2,
            (flags & 0x40) != 0, wcgUser, activeMode == 1,
            activeMode == 2 ? "HDR" : activeMode == 1 ? "WCG" : activeMode == 0 ? "SDR" : "Unknown");
    }
}

/// <summary>Read-only facts for one active Windows display path. Nullable fields
/// mean the platform did not expose the fact; they never authorize HDR.</summary>
public sealed record DisplayCapability(
    string SourceName = "", string FriendlyName = "", string PnpId = "",
    uint AdapterLow = 0, int AdapterHigh = 0, uint SourceId = 0, uint TargetId = 0,
    uint OutputTechnology = 0, bool? HdrSupported = null, bool? HdrActive = null,
    bool? WcgSupported = null, bool? WcgActive = null, string ActiveColorMode = "Unknown",
    uint? BitsPerColor = null, int? DxgiColorSpace = null,
    float? ReportedMinLuminance = null, float? ReportedMaxLuminance = null, float? ReportedMaxFullFrameLuminance = null,
    string DolbyDisplayMode = "Unknown / not exposed", string? DxgiAdapter = null,
    string? EdidFingerprint = null, string? EdidExtensions = null,
    float[]? RedPrimary = null, float[]? GreenPrimary = null, float[]? BluePrimary = null,
    float[]? WhitePoint = null, bool? AdvancedColorSupported = null,
    bool? AdvancedColorActive = null, bool? AdvancedColorLimitedByPolicy = null,
    bool? HdrUserEnabled = null, bool? WcgUserEnabled = null, uint? ColorEncoding = null,
    double? RefreshRateHz = null)
{
    // DXGI 12 is PQ/BT.2020; 1 is linear scRGB. An SDR or unmatched DXGI
    // descriptor cannot corroborate Windows' active HDR mode.
    public bool WindowsHdrPathActive => HdrSupported == true && HdrActive == true &&
        ActiveColorMode == "HDR" && DxgiColorSpace is 12 or 1;

    // Windows WCG is SDR signalling with FP16 color-managed composition. An
    // output descriptor without a matched EDID, active WCG, 10-bit path and
    // OS-reported wide primaries cannot authorize this separate renderer class.
    public int? QualifiedWcgPeakNits
    {
        get
        {
            if (WindowsHdrPathActive || WcgSupported != true || WcgActive != true ||
                AdvancedColorActive != true || ActiveColorMode != "WCG" ||
                BitsPerColor < 10 || DxgiColorSpace != 0 ||
                string.IsNullOrWhiteSpace(PnpId) || EdidFingerprint?.Length != 64 ||
                !EdidFingerprint.All(Uri.IsHexDigit) ||
                RedPrimary is not { Length: 2 } red || GreenPrimary is not { Length: 2 } green ||
                BluePrimary is not { Length: 2 } blue ||
                !Near(red, .68, .32) || !Near(green, .265, .69) || !Near(blue, .15, .06) ||
                ReportedMaxLuminance is not float max || !float.IsFinite(max) ||
                max < 250 || max > 1000) return null;
            if (ReportedMaxFullFrameLuminance is float full)
            {
                if (!float.IsFinite(full) || full < 250 || full > 1000) return null;
                max = Math.Min(max, full);
            }
            return (int)Math.Floor(max / 10) * 10;
        }
    }

    private static bool Near(float[] xy, double x, double y) =>
        float.IsFinite(xy[0]) && float.IsFinite(xy[1]) &&
        Math.Abs(xy[0] - x) <= .03 && Math.Abs(xy[1] - y) <= .03;
}

public enum ColorDelivery { Sdr, Pq, Hlg, Hdr10, Unknown }

public sealed record DisplayColorPlan(ColorDelivery Source, ColorDelivery RendererTarget,
    bool ToneMapToSdr, bool RtxVideoHdrEligible, string Reason, int? TargetPeakNits = null);

public static class DisplayColorPolicy
{
    public static DisplayColorPlan Decide(MediaInfo source, DisplayCapability? display)
    {
        bool hdrPath = display?.WindowsHdrPathActive == true;
        if (source.Transfer is "pq" or "hlg")
        {
            var kind = source.Transfer == "pq" ? ColorDelivery.Pq : ColorDelivery.Hlg;
            if (hdrPath)
                return new(kind, kind, false, false, "Windows HDR is active on the matched target; the renderer is asked to preserve source HDR.");
            if (display?.QualifiedWcgPeakNits is int peak)
                return new(kind, ColorDelivery.Sdr, true, false,
                    $"Windows WCG is active on the matched 10-bit display; libplacebo is asked for high-luminance wide-gamut SDR at the OS-reported {peak}-nit peak. Physical luminance remains unmeasured.",
                    peak);
            return new(kind, ColorDelivery.Sdr, true, false,
                "The matched target has no verified active HDR or qualified WCG path; the renderer is asked to tone-map to conventional SDR.");
        }
        if (source.IsKnownSdr)
            return new(ColorDelivery.Sdr, ColorDelivery.Sdr, false, hdrPath,
                hdrPath ? "Known SDR on a verified active HDR target; optional RTX Video HDR may be requested."
                    : "Known SDR stays SDR; no verified active HDR target is available.");
        return new(ColorDelivery.Unknown, hdrPath ? ColorDelivery.Unknown : ColorDelivery.Sdr, false, false,
            "Source transfer is unknown; HDR conversion is withheld and renderer output awaits observation.");
    }
}

public static class DisplayCapabilitySelection
{
    public static DisplayCapability? ForScreen(IReadOnlyList<DisplayCapability> paths, string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return null;
        var matches = paths.Where(x => x.SourceName.Equals(sourceName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static DisplayCapability? ForAttempt(IReadOnlyList<DisplayCapability> paths, DisplayCapability? planned)
    {
        if (planned is null) return null;
        var current = ForScreen(paths, planned.SourceName);
        return current?.AdapterLow == planned.AdapterLow && current.AdapterHigh == planned.AdapterHigh &&
            current.SourceId == planned.SourceId && current.TargetId == planned.TargetId &&
            current.PnpId.Equals(planned.PnpId, StringComparison.OrdinalIgnoreCase) &&
            current.EdidFingerprint == planned.EdidFingerprint ? current : null;
    }
}
