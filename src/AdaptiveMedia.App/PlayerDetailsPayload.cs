using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaptiveMedia;

/// <summary>What DemiMedia tells the in-player details panel
/// (payload/mpv-config/scripts/demimedia_details.lua) about one playback: the
/// probed source, and the requested, planned, health, fallback and recovery
/// lines of the truth chain, verbatim. Display data only. Nothing here is an
/// observation: the panel reads its Observed rows from mpv itself, and nothing
/// reads this value back as evidence.</summary>
public sealed record PlayerDetailsPayload(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("source")] IReadOnlyList<string[]> Source,
    [property: JsonPropertyName("requested")] IReadOnlyList<string> Requested,
    [property: JsonPropertyName("planned")] IReadOnlyList<string> Planned,
    [property: JsonPropertyName("health")] PlayerDetailsHealth? Health,
    [property: JsonPropertyName("fallback")] IReadOnlyList<string> Fallback,
    [property: JsonPropertyName("recovery")] IReadOnlyList<string> Recovery)
{
    /// <summary>The persistent mpv property the panel observes. A property rather
    /// than a script message, so a value set before the script has loaded is not
    /// lost. Its own namespace: user-data/adaptive/* is delivery evidence and is
    /// never written by the app.</summary>
    public const string Property = "user-data/demimedia/plan";

    /// <summary>The shape the panel understands; bump on an incompatible change.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The same serialization the IPC command uses, so equal payloads
    /// compare equal and an unchanged payload is never pushed twice.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>The current health line, whether it needs the viewer's attention, and
/// any further health lines (for example what happened earlier in the attempt).</summary>
public sealed record PlayerDetailsHealth(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("attention")] bool Attention,
    [property: JsonPropertyName("lines")] IReadOnlyList<string> Lines);

/// <summary>Builds <see cref="PlayerDetailsPayload"/>. Pure: it reads a plan and,
/// once playback has started, that playback's truth report, and invents nothing.
/// A source fact the probe did not establish is shown as Unknown.</summary>
public static class PlayerDetailsPayloadBuilder
{
    public const string Unknown = "Unknown";

    /// <summary>Health states that are ordinary, so the panel stays quiet. Taken
    /// from the truth vocabulary itself rather than restated.</summary>
    private static readonly string[] QuietHealth = new[]
    {
        SustainedPlaybackHealth.Starting, SustainedPlaybackHealth.Healthy,
        SustainedPlaybackHealth.EndedNormally, SustainedPlaybackHealth.UserStopped,
    }.Select(HealthText.Describe).ToArray();

    /// <summary>What the truth chain writes when a section has nothing to report.</summary>
    private const string NoRecovery = "None";

    /// <summary>With a truth report, every line is that report's own. Without one
    /// (before an attempt exists), Requested and Planned come from the same truth
    /// builder applied to the plan alone, and nothing about health is claimed.</summary>
    public static PlayerDetailsPayload Build(PlaybackPlan plan, PlaybackTruthReport? truth = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var report = truth ?? PlaybackTruthBuilder.Build(
            new PlaybackAttemptEvidence(0, 1, plan, PlaybackAttemptKind.Stable, "planned"), [], []);
        PlayerDetailsHealth? health = null;
        if (truth is not null && truth.Health.Lines.Count > 0)
        {
            string state = truth.Health.Lines[0];
            health = new(state, !IsQuiet(state), truth.Health.Lines.Skip(1).ToArray());
        }
        // Fallback lines exist only for paths that really fell back; the section's
        // "None observed" placeholder is not a line worth drawing.
        string[] fallback = truth is not null && truth.Delivery.Any(v => v.State == DeliveryState.FellBack)
            ? truth.Fallback.Lines.ToArray() : [];
        string[] recovery = truth is null || truth.Recovery.Lines.SequenceEqual([NoRecovery]) ? [] : truth.Recovery.Lines.ToArray();
        return new(PlayerDetailsPayload.CurrentVersion, SourceRows(plan.Source), report.Requested.Lines.ToArray(),
            report.Planned.Lines.ToArray(), health, fallback, recovery);
    }

    /// <summary>True for a health line that needs no attention.</summary>
    public static bool IsQuiet(string healthLine) => QuietHealth.Contains(healthLine, StringComparer.Ordinal);

    /// <summary>Label and value pairs for what the probe established about the
    /// source. Each value is either a probed fact or <see cref="Unknown"/>.</summary>
    public static IReadOnlyList<string[]> SourceRows(MediaInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string video = source.Known
            ? $"{source.Width} × {source.Height}" + (source.Fps > 0 && double.IsFinite(source.Fps)
                ? " · " + source.Fps.ToString("0.###", CultureInfo.InvariantCulture) + " fps" : "")
            : Unknown;
        var codec = new List<string>();
        if (Has(source.Codec)) codec.Add(source.Codec);
        if (codec.Count > 0 && Has(source.PixelFormat)) codec.Add(source.PixelFormat);
        // HDR and SDR are claimed only from a transfer the probe reported and the
        // plan itself classifies; any other transfer is shown by name, unjudged.
        var colour = new List<string>();
        if (source.IsHdr) colour.Add("HDR");
        else if (source.IsKnownSdr) colour.Add("SDR");
        if (Has(source.Transfer)) colour.Add(source.Transfer);
        if (Has(source.Primaries)) colour.Add(source.Primaries);
        return
        [
            ["Video", video],
            ["Codec", codec.Count > 0 ? string.Join(" · ", codec) : Unknown],
            ["Colour", colour.Count > 0 ? string.Join(" · ", colour) : Unknown],
            ["Audio", Has(source.AudioCodec) ? source.AudioCodec : Unknown],
        ];
    }

    private static bool Has(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Equals("unknown", StringComparison.OrdinalIgnoreCase);
}
