using AdaptiveMedia;
using System.Security.Cryptography;
using System.Text;

internal static class WatchLaterResumeTests
{
    public static int Run(Action<bool, string> check)
    {
        int count = 0;
        void Check(bool value, string label) { count++; check(value, "USER RESUME: " + label); }
        string root = Path.Combine(Path.GetTempPath(), "demimedia-resume-" + Guid.NewGuid().ToString("N"));
        string owned = Path.Combine(root, "owned");
        string legacy = Path.Combine(root, "legacy");
        string? previousMpvHome = Environment.GetEnvironmentVariable("MPV_HOME");
        Environment.SetEnvironmentVariable("MPV_HOME", null);
        Directory.CreateDirectory(owned); Directory.CreateDirectory(legacy);
        try
        {
            string portablePlayer = Path.Combine(root, "player", "mpv.exe");
            string portableConfig = Path.Combine(root, "player", "portable_config");
            Directory.CreateDirectory(portableConfig);
            Check(WatchLaterResume.LegacyDirectory(portablePlayer) == Path.Combine(portableConfig, "watch_later"),
                "mpv portable_config is the legacy watch-later authority when present");
            string source = Path.Combine(root, "film.mkv");
            string other = Path.Combine(root, "other.mkv");
            File.WriteAllText(source, "fixture"); File.WriteAllText(other, "different fixture");
            PlaybackPlan Plan(string item) => PlaybackPlanBuilder.Build("mpv.exe", "config", [item],
                new("Reference", "Off", "Off", false, false), new(1920, 1080, DurationSeconds: 120),
                new(1920, 1080), new(false, false), "resume-test");
            string State(string item) => Path.Combine(owned, Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(item)))));
            File.WriteAllText(State(other), "start=42.000000\n");
            Check(WatchLaterResume.Find(Plan(source), owned, legacy) is null,
                "a state file for another source cannot be offered");
            File.WriteAllText(State(source), "start=42.000000\n");
            File.SetLastWriteTimeUtc(State(source), DateTime.UtcNow);
            var offer = WatchLaterResume.Find(Plan(source), owned, legacy);
            Check(offer is { PositionSeconds: 42 }, "matching mpv watch-later position is offered");
            var resumed = WatchLaterResume.ApplyChoice(Plan(source), offer, resume: true, owned);
            var beginning = WatchLaterResume.ApplyChoice(Plan(source), offer, resume: false, owned);
            Check(resumed.Arguments.Contains("--start=42") && resumed.Arguments.Contains("--resume-playback=no") &&
                  resumed.UserResumeAt == 42 && resumed.RecoveryResumeAt is null,
                "Resume launches the chosen source at the user position, distinct from recovery");
            Check(!beginning.Arguments.Any(x => x.StartsWith("--start=")) && beginning.Arguments.Contains("--resume-playback=no") &&
                  beginning.UserResumeAt is null,
                "Start from beginning cannot inherit mpv's automatic watch-later seek");
            bool wrongChoiceRejected = false;
            try { WatchLaterResume.ApplyChoice(Plan(source), new(other, 42, State(other), DateTime.UtcNow), true, owned); }
            catch (InvalidOperationException) { wrongChoiceRejected = true; }
            Check(wrongChoiceRejected, "a wrong-source offer cannot be applied even if passed directly to launch planning");
            File.SetLastWriteTimeUtc(State(source), DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
            Check(WatchLaterResume.Find(Plan(source), owned, legacy) is null,
                "state older than the modified source is stale");
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(-1));
            foreach (string malformed in new[] { "start=NaN\n", "start=-1\n", "start=42\nstart=43\n", "start=bad\n" })
            {
                File.WriteAllText(State(source), malformed);
                Check(WatchLaterResume.Find(Plan(source), owned, legacy) is null,
                    "malformed watch-later state is rejected: " + malformed.Trim());
            }
            File.WriteAllText(State(source), "# " + other + "\nstart=42\n");
            Check(WatchLaterResume.Find(Plan(source), owned, legacy) is null,
                "a mismatched filename comment defeats a matching hash");
            File.WriteAllText(State(source), "start=116\n");
            Check(WatchLaterResume.Find(Plan(source), owned, legacy) is null,
                "a point near EOF starts from beginning instead of offering a trivial resume");
            File.Delete(State(source));
            string legacyState = Path.Combine(legacy, Path.GetFileName(State(source)));
            File.WriteAllText(legacyState, "start=35\n");
            Check(WatchLaterResume.Find(Plan(source), owned, legacy) is { PositionSeconds: 35 },
                "existing mpv watch-later authority remains readable without modifying it");
            return count;
        }
        finally { Environment.SetEnvironmentVariable("MPV_HOME", previousMpvHome); Directory.Delete(root, true); }
    }
}
