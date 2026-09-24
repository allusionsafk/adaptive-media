using AdaptiveMedia;
using System.Text.Json;

/// <summary>The in-player details payload: source facts or Unknown, never
/// invented; requested and planned lines verbatim from the truth chain and never
/// mixed with observation; quiet health unless something needs attention; and a
/// stable JSON shape the mpv panel can read.</summary>
internal static class PlayerDetailsPayloadTests
{
    private static readonly MediaInfo Sdr1080 = new(1920, 1080, 23.976, "h264", "bt.1886", "bt.709", "aac", "yuv420p");
    private static readonly PlaybackOptions RtxSmooth = new("Enhanced", "RtxVsr", "Smooth", true, false);

    private static PlaybackPlan Plan(PlaybackCapabilities caps, MediaInfo? source = null, PlaybackOptions? options = null) =>
        PlaybackPlanBuilder.Build("mpv.exe", "config", ["movie.mkv"], options ?? RtxSmooth, source ?? Sdr1080,
            new PlaybackTarget(2560, 1440, Fullscreen: true), caps, "details-test");

    private static PlaybackAttemptEvidence Playing(PlaybackPlan plan, string json, SustainedPlaybackHealth state,
        SustainedPlaybackHealth? worst = null)
    {
        var e = new PlaybackAttemptEvidence(1, 1, plan, PlaybackAttemptKind.Stable, "stable player") { Started = true };
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        // Two polls with position advancing, as a player showing frames would report.
        e.Observe(new Dictionary<string, JsonElement>(props) { ["time-pos"] = JsonSerializer.SerializeToElement(10.0) }, null);
        props["time-pos"] = JsonSerializer.SerializeToElement(11.0);
        e.Observe(props, null);
        e.TrackHealth(() => new PlaybackHealthSnapshot { AttemptId = 1, State = state, WorstCondition = worst ?? state, PlaybackProgressed = true });
        return e;
    }

    private static string Value(IReadOnlyList<string[]> rows, string label) => rows.Single(r => r[0] == label)[1];

    public static int Run(Action<bool, string> check)
    {
        int count = 0;
        void Check(bool v, string m) { count++; check(v, "DETAILS: " + m); }
        var noRtx = new PlaybackCapabilities(true, false);
        const string NvdecPoll = """{"hwdec-current":"nvdec","gpu-api":"vulkan","current-vo":"gpu-next","vf":[],"estimated-display-fps":239.9}""";

        // Source: probed facts, or Unknown. Never a guess.
        {
            var unknown = PlayerDetailsPayloadBuilder.SourceRows(new MediaInfo());
            Check(unknown.Select(r => r[0]).SequenceEqual(["Video", "Codec", "Colour", "Audio"]), "source rows have stable labels");
            Check(unknown.All(r => r[1] == PlayerDetailsPayloadBuilder.Unknown), "an unprobed source says Unknown for every fact");
            var sdr = PlayerDetailsPayloadBuilder.SourceRows(Sdr1080);
            Check(Value(sdr, "Video") == "1920 × 1080 · 23.976 fps" && Value(sdr, "Codec") == "h264 · yuv420p" && Value(sdr, "Audio") == "aac",
                "probed dimensions, frame rate, codec and audio are shown as probed");
            Check(Value(sdr, "Colour") == "SDR · bt.1886 · bt.709", "a known SDR transfer is SDR");
            var pq = PlayerDetailsPayloadBuilder.SourceRows(new(3840, 2160, 24, "hevc", "pq", "bt.2020", "eac3", "yuv420p10"));
            Check(Value(pq, "Colour") == "HDR · pq · bt.2020" && Value(pq, "Video") == "3840 × 2160 · 24 fps", "PQ is HDR");
            Check(Value(PlayerDetailsPayloadBuilder.SourceRows(new(3840, 2160, 50, "hevc", "hlg", "bt.2020")), "Colour").StartsWith("HDR · hlg", StringComparison.Ordinal),
                "HLG is HDR");
            var unspecified = PlayerDetailsPayloadBuilder.SourceRows(new(1920, 1080, 0, "h264", "unknown", "bt.709"));
            Check(Value(unspecified, "Colour") == "bt.709" && Value(unspecified, "Video") == "1920 × 1080" && Value(unspecified, "Audio") == "Unknown",
                "an unreported transfer claims neither HDR nor SDR, and an unknown frame rate is left out");
            var vlog = Value(PlayerDetailsPayloadBuilder.SourceRows(new(1920, 1080, 25, "prores", "v-log", "v-gamut")), "Colour");
            Check(vlog == "v-log · v-gamut", "an unclassified transfer is shown by name, unjudged");
            Check(PlayerDetailsPayloadBuilder.Build(Plan(noRtx, new MediaInfo())).Source.All(r => r[1] == "Unknown"),
                "a plan for an unprobed source (a URL, say) says Unknown");
        }

        // Requested and planned are the truth chain's own lines; observation never enters.
        {
            var plan = Plan(noRtx);
            var truth = PlaybackTruthBuilder.Build(Playing(plan, NvdecPoll, SustainedPlaybackHealth.Healthy), [], []);
            var payload = PlayerDetailsPayloadBuilder.Build(plan, truth);
            Check(payload.Requested.SequenceEqual(truth.Requested.Lines) && payload.Planned.SequenceEqual(truth.Planned.Lines),
                "requested and planned are the truth report's lines, verbatim");
            Check(payload.Requested.Contains("RTX Super Resolution") && !payload.Planned.Any(l => l.Contains("RTX", StringComparison.Ordinal)),
                "an RTX request that was not planned stays a request");
            Check(truth.Observed.Lines.Any(l => l.Contains("NVDEC", StringComparison.Ordinal)), "fixture: the truth report did observe something");
            var every = payload.Requested.Concat(payload.Planned).Concat(payload.Fallback).Concat(payload.Recovery)
                .Concat(payload.Health?.Lines ?? []).Concat(payload.Source.Select(r => r[1]));
            Check(!every.Intersect(truth.Observed.Lines).Any(), "no observed line is carried as a request, plan or fact");
            using var json = JsonDocument.Parse(payload.ToJson());
            Check(!json.RootElement.EnumerateObject().Any(p => p.Name.Contains("observ", StringComparison.OrdinalIgnoreCase)),
                "the payload has no observed field; the panel reads observation from mpv itself");

            var preview = PlayerDetailsPayloadBuilder.Build(plan);
            Check(preview.Requested.SequenceEqual(truth.Requested.Lines) && preview.Planned.SequenceEqual(truth.Planned.Lines),
                "before playback, requested and planned come from the same truth wording");
            Check(preview.Health is null && preview.Fallback.Count == 0 && preview.Recovery.Count == 0,
                "before playback nothing about health, fallback or recovery is claimed");
        }

        // Health is quiet while healthy; attention only when something needs it.
        {
            var plan = Plan(noRtx);
            PlayerDetailsHealth HealthOf(SustainedPlaybackHealth state, SustainedPlaybackHealth? worst = null) =>
                PlayerDetailsPayloadBuilder.Build(plan, PlaybackTruthBuilder.Build(Playing(plan, NvdecPoll, state, worst), [], [])).Health!;
            var healthy = HealthOf(SustainedPlaybackHealth.Healthy);
            Check(healthy.State == "Healthy" && !healthy.Attention && healthy.Lines.Count == 0, "healthy playback reads plainly 'Healthy', without attention");
            Check(!HealthOf(SustainedPlaybackHealth.Starting).Attention, "starting is not an attention state");
            var recovered = HealthOf(SustainedPlaybackHealth.Healthy, SustainedPlaybackHealth.TransientPressure);
            Check(!recovered.Attention && recovered.Lines.SequenceEqual(["Earlier in this attempt: Temporary presentation pressure"]),
                "an earlier episode is kept as context while the current state stays quiet");
            var pressure = HealthOf(SustainedPlaybackHealth.SustainedDegradation);
            Check(pressure.Attention && pressure.State == "Sustained presentation pressure", "sustained pressure asks for attention, in truth wording");
            Check(HealthOf(SustainedPlaybackHealth.Stalled).Attention && HealthOf(SustainedPlaybackHealth.TransientPressure).Attention,
                "stalls and pressure are attention states");
            Check(PlayerDetailsPayloadBuilder.IsQuiet("Ended normally") && !PlayerDetailsPayloadBuilder.IsQuiet("Runtime failure"),
                "quiet health is the truth vocabulary's own words");
        }

        // Fallback and recovery appear only when they happened.
        {
            var plan = Plan(noRtx);
            var calm = PlayerDetailsPayloadBuilder.Build(plan, PlaybackTruthBuilder.Build(Playing(plan, NvdecPoll, SustainedPlaybackHealth.Healthy), [], []));
            Check(calm.Fallback.Count == 0 && calm.Recovery.Count == 0, "nothing fell back and nothing recovered: no placeholder lines");
            var wrongDecoder = PlaybackTruthBuilder.Build(Playing(plan, """{"hwdec-current":"d3d11va","current-vo":"gpu-next","vf":[]}""",
                SustainedPlaybackHealth.Healthy), [], []);
            Check(wrongDecoder.Delivery.Any(v => v.State == DeliveryState.FellBack), "fixture: a decoder other than the planned one fell back");
            Check(PlayerDetailsPayloadBuilder.Build(plan, wrongDecoder).Fallback.SequenceEqual(wrongDecoder.Fallback.Lines),
                "fallback lines are the truth report's, verbatim");
            var record = new PlaybackRecoveryRecord(1, "stable player", "Stalled", "NoRecoveryAvailable", null, "none",
                "Stable playback failed and there is no other verified player to recover to; it is not relaunched automatically.");
            var stalled = PlaybackTruthBuilder.Build(Playing(plan, NvdecPoll, SustainedPlaybackHealth.Stalled), [], [record]);
            var withRecovery = PlayerDetailsPayloadBuilder.Build(plan, stalled);
            Check(withRecovery.Recovery.SequenceEqual(stalled.Recovery.Lines) && withRecovery.Recovery.Single().EndsWith("→ Unavailable for this player", StringComparison.Ordinal),
                "recovery lines are the truth report's, verbatim");
        }

        // A stable, versioned shape that survives the IPC command's serialization as an object.
        {
            var plan = Plan(noRtx);
            var truth = PlaybackTruthBuilder.Build(Playing(plan, NvdecPoll, SustainedPlaybackHealth.Healthy), [], []);
            string first = PlayerDetailsPayloadBuilder.Build(plan, truth).ToJson();
            Check(first == PlayerDetailsPayloadBuilder.Build(plan, truth).ToJson(), "the same playback state serializes identically, so it is pushed once");
            using var doc = JsonDocument.Parse(first);
            var root = doc.RootElement;
            Check(root.EnumerateObject().Select(p => p.Name).SequenceEqual(["version", "source", "requested", "planned", "health", "fallback", "recovery"]),
                "payload keys are the panel's lowercase names");
            Check(root.GetProperty("version").GetInt32() == PlayerDetailsPayload.CurrentVersion &&
                  root.GetProperty("source").EnumerateArray().All(r => r.GetArrayLength() == 2) &&
                  root.GetProperty("health").GetProperty("attention").ValueKind == JsonValueKind.False,
                "source rows are label/value pairs and health carries its attention flag");
            // MpvIpc serializes the command as object[]; the payload must arrive as a JSON object, not a string.
            object[] command = ["set_property", PlayerDetailsPayload.Property, PlayerDetailsPayloadBuilder.Build(plan, truth)];
            using var sent = JsonDocument.Parse(JsonSerializer.Serialize(new { command, request_id = 1 }));
            var value = sent.RootElement.GetProperty("command")[2];
            Check(value.ValueKind == JsonValueKind.Object && value.GetProperty("planned").GetArrayLength() == truth.Planned.Lines.Count,
                "the IPC command carries the payload as a JSON object");
            Check(PlayerDetailsPayload.Property == "user-data/demimedia/plan" && !PlayerDetailsPayload.Property.StartsWith("user-data/adaptive/", StringComparison.Ordinal),
                "the panel's property never touches delivery evidence (user-data/adaptive/*)");
        }
        return count;
    }
}
