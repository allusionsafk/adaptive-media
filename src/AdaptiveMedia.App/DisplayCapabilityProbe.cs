using AdaptiveMedia.Native;
using Microsoft.Win32;
using System.Security.Cryptography;

namespace AdaptiveMedia;

/// <summary>Read-only DisplayConfig, DXGI and EDID inventory. DisplayConfig
/// source GDI name and adapter LUID must both match the DXGI output.</summary>
internal static class DisplayCapabilityProbe
{
    public static IReadOnlyList<DisplayCapability> Read()
    {
        DisplayColorInfo[] paths;
        try { paths = HdrController.GetDisplays(); }
        catch { return []; }
        IReadOnlyList<DxgiOutputInfo> outputs;
        try { outputs = DxgiOutputProbe.Read(); }
        catch { outputs = []; }
        return paths.Select(path =>
        {
            var matches = outputs.Where(x => x.Attached &&
                x.AdapterLow == path.AdapterLow && x.AdapterHigh == path.AdapterHigh &&
                x.DeviceName.Equals(path.SourceName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            var dxgi = matches.Length == 1 ? matches[0] : null;
            var (pnp, fingerprint, extensions) = ReadEdid(path.MonitorDevicePath);
            return new DisplayCapability(path.SourceName ?? "", path.Name ?? "", pnp,
                path.AdapterLow, path.AdapterHigh, path.SourceId, path.TargetId,
                path.OutputTechnology, path.HdrSupported, path.HdrActive, path.WcgSupported,
                path.WcgActive, path.ActiveColorMode ?? "Unknown",
                dxgi?.BitsPerColor ?? path.BitsPerColor, dxgi?.ColorSpace,
                dxgi?.MinLuminance, dxgi?.MaxLuminance, dxgi?.MaxFullFrameLuminance,
                DxgiAdapter: dxgi?.AdapterName, EdidFingerprint: fingerprint,
                EdidExtensions: extensions, RedPrimary: dxgi?.Red, GreenPrimary: dxgi?.Green,
                BluePrimary: dxgi?.Blue, WhitePoint: dxgi?.White,
                AdvancedColorSupported: path.AdvancedColorSupported,
                AdvancedColorActive: path.AdvancedColorActive,
                AdvancedColorLimitedByPolicy: path.AdvancedColorLimitedByPolicy,
                HdrUserEnabled: path.HdrUserEnabled, WcgUserEnabled: path.WcgUserEnabled,
                ColorEncoding: path.ColorEncoding, RefreshRateHz: path.RefreshRateHz);
        }).ToArray();
    }

    private static (string Pnp, string? Fingerprint, string? Extensions) ReadEdid(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return ("", null, null);
        string[] parts = devicePath.Split('#');
        if (parts.Length < 3 || !parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase))
            return ("", null, null);
        string pnp = $"DISPLAY\\{parts[1]}\\{parts[2]}";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Enum\\{pnp}\\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] bytes || bytes.Length < 128 || bytes.Length % 128 != 0)
                return (pnp, null, null);
            bool valid = true;
            for (int block = 0; block < bytes.Length / 128; block++)
                if (bytes.Skip(block * 128).Take(128).Sum(x => (int)x) % 256 != 0) valid = false;
            if (!valid) return (pnp, null, "Invalid checksum");
            string tags = string.Join(",", Enumerable.Range(1, bytes.Length / 128 - 1).Select(i =>
                bytes[i * 128] == 0x70 ? "DisplayID (0x70)" : $"0x{bytes[i * 128]:X2}"));
            return (pnp, Convert.ToHexString(SHA256.HashData(bytes)), tags.Length == 0 ? "Base EDID" : tags);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return (pnp, null, null); }
    }
}
