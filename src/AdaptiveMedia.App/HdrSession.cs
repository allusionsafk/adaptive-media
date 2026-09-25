namespace AdaptiveMedia;

/// <summary>Windows HDR switching is unavailable until a target-bound,
/// compare-and-restore transaction is qualified. Preparation and launch are
/// read-only, including when an old recovery file exists.</summary>
internal sealed class HdrSession : IDisposable
{
    public static HdrSession Begin(PlaybackPlan plan, SessionDiagnostics report)
    {
        if (plan.Requested.AutoHdrSwitch)
            report.FallbackHistory.Add("Automatic Windows HDR switching is unavailable; Windows display state was not changed.");
        if (File.Exists(Path.Combine(SettingsStore.DirectoryPath, "hdr-recovery.json")))
            report.FallbackHistory.Add("A legacy HDR recovery record was ignored; current Windows display state is authoritative.");
        return new HdrSession();
    }

    public void Dispose() { }
}
