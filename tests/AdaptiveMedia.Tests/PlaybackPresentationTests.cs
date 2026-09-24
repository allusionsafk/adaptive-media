using AdaptiveMedia;

/// <summary>The launcher's presentation rules select and name recorded facts and
/// invent none: an unprobed source stays unknown, verdict rows are matched by the
/// truth chain's own text, activity never repeats the plan, and only persistent or
/// failed health asks for attention.</summary>
internal static class PlaybackPresentationTests
{
    public static int Run(Action<bool, string> check)
    {
        int count = 0;
        void Check(bool value, string message) { count++; check(value, message); }

        Check(PlaybackPresentation.MediaTitle([@"D:\Films\Harbour Lights.mkv"]) == "Harbour Lights.mkv", "A file is titled by its name");
        Check(PlaybackPresentation.MediaTitle([@"D:\Films\Season 2\"]) == "Season 2", "A folder is titled by its name");
        Check(PlaybackPresentation.MediaTitle(["a.mkv", "b.mkv", "c.mkv"]) == "3 items", "Several items are counted, not listed");
        Check(PlaybackPresentation.MediaTitle(["https://media.example/clips/intro%20cut.mp4"]) == "intro cut.mp4 · media.example",
            "A stream is titled by its last segment and host");
        Check(PlaybackPresentation.MediaTitle(["https://media.example/"]) == "media.example", "A bare host stays readable");

        var (source, plan) = PlaybackPresentation.SplitSummary("1920 × 1080 · SDR / unspecified\nSource-faithful scaling · PCM audio\nReason one.");
        Check(source == "1920 × 1080 · SDR / unspecified" && plan.Count == 2 && plan[1] == "Reason one.", "The summary splits into source and plan");

        var unknown = new MediaInfo();
        Check(PlaybackPresentation.SourceFacts(unknown).Single().Value.StartsWith("Unknown", StringComparison.Ordinal),
            "An unprobed source is unknown, never estimated");
        Check(PlaybackPresentation.SourceLine(unknown, "Source dimensions unknown") == "Source dimensions unknown",
            "An unprobed source keeps the plan's own wording");
        var sdr = new MediaInfo(1920, 1080, 24, "h264", "bt.1886", "bt.709", "aac");
        Check(PlaybackPresentation.SourceLine(sdr, "") == "1920 × 1080 · 24 fps · SDR", "A known SDR source is named SDR");
        var hdr = new MediaInfo(3840, 2160, 23.976, "hevc", "pq", "bt.2020", "eac3");
        Check(PlaybackPresentation.SourceLine(hdr, "").EndsWith("· HDR", StringComparison.Ordinal), "A PQ source is named HDR");
        var unspecified = new MediaInfo(1280, 720, 30, "h264", "unknown", "unknown");
        Check(PlaybackPresentation.SourceLine(unspecified, "").EndsWith("Dynamic range not specified", StringComparison.Ordinal),
            "An untagged source does not claim SDR");
        var facts = PlaybackPresentation.SourceFacts(sdr);
        Check(facts.Any(f => f.Label == "Video codec" && f.Machine && f.Value == "h264"), "Codec names are machine values");
        Check(facts.Any(f => f.Label == "Dynamic range" && !f.Machine && f.Value == "SDR"), "Dynamic range is a human label");
        Check(PlaybackPresentation.SourceFacts(unspecified).All(f => f.Label != "Colour"), "Unknown colour metadata is omitted, not filled in");

        var verified = new DeliveryVerdict(DeliveryFeature.HardwareDecoding, "NVDEC hardware decoding", DeliveryState.Verified, "hwdec-current = nvdec");
        var bare = new DeliveryVerdict(DeliveryFeature.Cleanup, "Banding reduction", DeliveryState.Unverified, "");
        var section = new PlaybackTruthSection("Observed", [verified.Describe(), bare.Describe(), "gpu-next · Vulkan active"]);
        var rows = PlaybackPresentation.Rows(section, [verified, bare]);
        Check(rows[0] == new TruthRow("NVDEC hardware decoding", DeliveryState.Verified, "hwdec-current = nvdec"), "A verdict line keeps its state and evidence apart");
        Check(rows[1].State == DeliveryState.Unverified && rows[1].Evidence == "", "A verdict without evidence still carries its state");
        Check(rows[2] == new TruthRow("gpu-next · Vulkan active"), "An observed fact is shown as written");
        var requested = new PlaybackTruthSection("Requested", ["NVDEC hardware decoding"]);
        Check(PlaybackPresentation.Rows(requested, [verified])[0].State is null, "A request is never given an observed state");
        Check(PlaybackPresentation.StateLabel(DeliveryState.FellBack) == "Fell back" && PlaybackPresentation.StateLabel(DeliveryState.Pending) == "Not yet observed",
            "State labels use the truth chain's vocabulary");

        const string summary = "1920 × 1080 · SDR\nHigh-quality conventional scaling · PCM audio";
        Check(PlaybackPresentation.ActivityText(summary + "\nPlayback health: Healthy", summary) == "Playback health: Healthy",
            "Activity drops the plan it repeats");
        Check(PlaybackPresentation.ActivityText(summary.Replace("\n", "\r\n") + "\r\nPlayback ended.", summary) == "Playback ended.",
            "Activity handles Windows line endings");
        Check(PlaybackPresentation.ActivityText("RTX playback failed. Retrying once.", summary) == "RTX playback failed. Retrying once.",
            "Status from another plan is shown whole");
        Check(PlaybackPresentation.ActivityText(summary, summary) == summary, "A bare summary is not reduced to nothing");

        Check(!PlaybackPresentation.NeedsAttention(SustainedPlaybackHealth.Healthy), "Healthy playback is quiet");
        Check(!PlaybackPresentation.NeedsAttention(SustainedPlaybackHealth.TransientPressure), "Brief pressure is quiet");
        Check(!PlaybackPresentation.NeedsAttention(SustainedPlaybackHealth.EndedNormally) && !PlaybackPresentation.NeedsAttention(SustainedPlaybackHealth.UserStopped),
            "An ordinary end is not an alarm");
        foreach (var state in new[] { SustainedPlaybackHealth.SustainedDegradation, SustainedPlaybackHealth.Stalled, SustainedPlaybackHealth.Frozen,
                     SustainedPlaybackHealth.SourceFailure, SustainedPlaybackHealth.RuntimeFailure, SustainedPlaybackHealth.RecoveryStopped })
            Check(PlaybackPresentation.NeedsAttention(state), state + " asks for attention");
        return count;
    }
}
