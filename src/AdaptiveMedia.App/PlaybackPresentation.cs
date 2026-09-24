namespace AdaptiveMedia;

/// <summary>One label and value about the source. <see cref="Machine"/> marks a value
/// that the player or probe produced verbatim (a codec name, a transfer function),
/// which the interface sets in the machine face.</summary>
public sealed record SourceFact(string Label, string Value, bool Machine = false);

/// <summary>One line of the truth chain as the details panel shows it. A delivery
/// verdict keeps its state and evidence apart so the state can be read at a glance;
/// every other line is shown as written.</summary>
public sealed record TruthRow(string Text, DeliveryState? State = null, string Evidence = "");

/// <summary>Pure presentation rules for the launcher. Nothing here computes a new
/// fact: it only selects, splits and names what the plan, the probe and the truth
/// chain already recorded, so the interface cannot claim more than they do.</summary>
public static class PlaybackPresentation
{
    /// <summary>The name a person would use for what they chose: a file name, a folder
    /// name, or a stream's host and last path segment.</summary>
    public static string MediaTitle(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return "";
        if (items.Count > 1) return $"{items.Count} items";
        string item = items[0].Trim();
        if (Uri.TryCreate(item, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            string last = uri.Segments.Length > 0 ? Uri.UnescapeDataString(uri.Segments[^1].Trim('/')) : "";
            return string.IsNullOrEmpty(last) ? uri.Host : $"{last} · {uri.Host}";
        }
        string trimmed = item.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>The plan summary's first line describes the source; the rest describes
    /// the path and its reasons.</summary>
    public static (string Source, IReadOnlyList<string> Plan) SplitSummary(string summary)
    {
        var lines = summary.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? ("", []) : (lines[0], lines[1..]);
    }

    /// <summary>One line about the source for the hero: size, rate and dynamic range
    /// from the probe, or the plan's own wording when the source was not probed.</summary>
    public static string SourceLine(MediaInfo source, string fallback)
    {
        if (!source.Known) return fallback;
        var parts = new List<string> { $"{source.Width} × {source.Height}" };
        if (source.Fps > 0) parts.Add($"{source.Fps:0.###} fps");
        parts.Add(source.IsHdr ? "HDR" : source.IsKnownSdr ? "SDR" : "Dynamic range not specified");
        return string.Join(" · ", parts);
    }

    /// <summary>What the player has added to the plan. Status text repeats the plan
    /// summary before its live lines; the details already show the plan, so only the
    /// remainder is activity. Text that does not start with the summary (a retry plan,
    /// for example) is returned whole.</summary>
    public static string ActivityText(string status, string planSummary)
    {
        string text = status.Replace("\r", "");
        string summary = planSummary.Replace("\r", "").TrimEnd('\n');
        if (summary.Length > 0 && text.StartsWith(summary, StringComparison.Ordinal))
            text = text[summary.Length..].TrimStart('\n');
        return text.Trim().Length == 0 ? status : text;
    }

    /// <summary>Source facts from the pre-playback probe. An unprobed source (a stream,
    /// or a probe that timed out) is reported as unknown, never estimated.</summary>
    public static IReadOnlyList<SourceFact> SourceFacts(MediaInfo source)
    {
        if (!source.Known) return [new("Picture", "Unknown until the player opens it")];
        var facts = new List<SourceFact>
        {
            new("Picture", source.Fps > 0 ? $"{source.Width} × {source.Height} · {source.Fps:0.###} fps" : $"{source.Width} × {source.Height}"),
        };
        if (Known(source.Codec)) facts.Add(new("Video codec", source.Codec, Machine: true));
        facts.Add(new("Dynamic range", source.IsHdr ? "HDR" : source.IsKnownSdr ? "SDR" : "Not specified by the file"));
        if (Known(source.Transfer) || Known(source.Primaries))
            facts.Add(new("Colour", $"{Value(source.Transfer)} · {Value(source.Primaries)}", Machine: true));
        if (Known(source.AudioCodec)) facts.Add(new("Audio codec", source.AudioCodec, Machine: true));
        return facts;
    }

    /// <summary>Splits a truth section into rows, matching delivery verdicts by the
    /// exact text the truth builder wrote for them.</summary>
    public static IReadOnlyList<TruthRow> Rows(PlaybackTruthSection section, IReadOnlyList<DeliveryVerdict> delivery)
    {
        var rows = new List<TruthRow>(section.Lines.Count);
        foreach (string line in section.Lines)
        {
            var verdict = delivery.FirstOrDefault(v => v.Describe() == line);
            rows.Add(verdict is null ? new(line) : new(verdict.Label, verdict.State, verdict.Evidence));
        }
        return rows;
    }

    /// <summary>The word shown for a delivery state. It matches the truth chain's own
    /// vocabulary, capitalised for a status label.</summary>
    public static string StateLabel(DeliveryState state) => state switch
    {
        DeliveryState.Verified => "Verified",
        DeliveryState.Unverified => "Unverified",
        DeliveryState.NotNeeded => "Not needed",
        DeliveryState.FellBack => "Fell back",
        _ => "Not yet observed",
    };

    /// <summary>Healthy and briefly pressured playback stays quiet; a condition that
    /// persists, stalls or fails asks for attention. Terminal states that simply end
    /// playback (the end of the media, a user stop) are not an alarm.</summary>
    public static bool NeedsAttention(SustainedPlaybackHealth state) => state is
        SustainedPlaybackHealth.SustainedDegradation or SustainedPlaybackHealth.Stalled or SustainedPlaybackHealth.Frozen or
        SustainedPlaybackHealth.SourceFailure or SustainedPlaybackHealth.RuntimeFailure or SustainedPlaybackHealth.RecoveryStopped;

    private static bool Known(string value) => !string.IsNullOrWhiteSpace(value) && value != "unknown";
    private static string Value(string value) => Known(value) ? value : "unspecified";
}
