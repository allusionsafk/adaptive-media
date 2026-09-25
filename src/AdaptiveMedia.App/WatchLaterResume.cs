using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AdaptiveMedia;

/// <summary>A user-saved mpv watch-later point for exactly one local source.</summary>
public sealed record WatchLaterOffer(string SourcePath, double PositionSeconds, string StatePath, DateTime StateWriteUtc);

public static class WatchLaterResume
{
    public static string OwnedDirectory(string dataDirectory) => Path.Combine(dataDirectory, "watch-later");

    /// <summary>mpv's existing Windows state authority. --config-dir changes
    /// configuration loading, but not the state directory. A portable_config next
    /// to mpv wins over LocalAppData. This path is read only by DemiMedia.</summary>
    public static string LegacyDirectory(string executable)
    {
        string? home = Environment.GetEnvironmentVariable("MPV_HOME");
        if (!string.IsNullOrWhiteSpace(home)) return Path.Combine(Path.GetFullPath(home), "watch_later");
        string portable = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "portable_config");
        if (Directory.Exists(portable)) return Path.Combine(portable, "watch_later");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv", "watch_later");
    }

    private static string? SingleLocalSource(PlaybackPlan plan)
    {
        string[] items = plan.Arguments.SkipWhile(x => x != "--").Skip(1).ToArray();
        return items.Length == 1 && File.Exists(items[0]) ? Path.GetFullPath(items[0]) : null;
    }

    public static WatchLaterOffer? Find(PlaybackPlan plan, string ownedDirectory, string legacyDirectory)
    {
        string? source = SingleLocalSource(plan);
        if (source is null) return null;
        string filename = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(source)));
        string owned = Path.Combine(ownedDirectory, filename);
        string legacy = Path.Combine(legacyDirectory, filename);
        string? state = File.Exists(owned) ? owned : File.Exists(legacy) ? legacy : null;
        if (state is null) return null;
        try
        {
            var media = new FileInfo(source);
            var saved = new FileInfo(state);
            if (!media.Exists || !saved.Exists || saved.Length is <= 0 or > 16384 ||
                media.LastWriteTimeUtc > saved.LastWriteTimeUtc.AddSeconds(2)) return null;
            string? start = null;
            foreach (string raw in File.ReadAllLines(state))
            {
                string line = raw.Trim();
                if (line.StartsWith("# ", StringComparison.Ordinal) &&
                    !line.Equals("# redirect entry", StringComparison.Ordinal) &&
                    !line[2..].Equals(source, StringComparison.OrdinalIgnoreCase)) return null;
                if (!line.StartsWith("start=", StringComparison.Ordinal)) continue;
                if (start is not null) return null;
                start = line[6..];
            }
            if (start is null || !double.TryParse(start, NumberStyles.Float, CultureInfo.InvariantCulture, out double at) ||
                !double.IsFinite(at) || at < 5 || at > 7 * 24 * 3600) return null;
            double duration = plan.Source.DurationSeconds;
            if (duration > 0 && at >= duration - Math.Min(30, Math.Max(5, duration * .02))) return null;
            return new(source, at, state, saved.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Apply the user's explicit choice. mpv's implicit resume is disabled
    /// in both branches, so an old state cannot silently override Start beginning.
    /// New Shift+Q state belongs to DemiMedia's data directory.</summary>
    public static PlaybackPlan ApplyChoice(PlaybackPlan plan, WatchLaterOffer? offer, bool resume, string ownedDirectory)
    {
        string? source = SingleLocalSource(plan);
        if (resume && (source is null || offer is null ||
            !source.Equals(offer.SourcePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The resume point does not belong to this media.");
        var args = plan.Arguments.Where(x => !x.StartsWith("--start=", StringComparison.Ordinal) &&
            !x.StartsWith("--resume-playback=", StringComparison.Ordinal) &&
            !x.StartsWith("--watch-later-dir=", StringComparison.Ordinal)).ToList();
        int split = args.IndexOf("--");
        if (split < 0) throw new InvalidOperationException("The playback plan has no source boundary.");
        args.Insert(split++, "--resume-playback=no");
        args.Insert(split++, "--watch-later-dir=" + Path.GetFullPath(ownedDirectory));
        if (resume) args.Insert(split, PlaybackRecoveryText.StartArgument(offer!.PositionSeconds));
        return plan with
        {
            Arguments = args.ToImmutableArray(),
            UserResumeAt = resume ? offer!.PositionSeconds : null,
            RecoveryResumeAt = null,
            Reasons = resume ? plan.Reasons.Add("User chose Resume near " + PlaybackRecoveryText.Position(offer!.PositionSeconds) + ".") : plan.Reasons,
        };
    }
}
