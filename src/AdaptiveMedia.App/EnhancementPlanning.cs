using System.Collections.Immutable;

namespace AdaptiveMedia;

public enum EnhancementMode { Automatic, Reference, Enhanced, Compatibility }
public enum AutomaticGoal { PreserveOriginal, ImproveDetail, SmootherMotion, CleanImage, BalancedImprovement }
public enum EnhancementStrength { Subtle, Normal, Strong }
public enum DetailIntent { Preserve, Balanced, Sharper, Maximum, Automatic }
public enum MotionIntent { Original, CadenceCorrected, BlendSmooth, GeneratedMotion, NeuralMotion, Automatic }
public enum CleanupIntent { PreserveTexture, Balanced, Clean, Automatic }
public enum PerformanceIntent { Efficient, Balanced, MaximumQuality }

public enum DetailImplementation { None, Conventional, NvidiaVpp }
public enum MotionImplementation { Original, CadenceCorrected, BlendSmooth, GeneratedMotion, NeuralMotion }
public enum CleanupImplementation { Off, Gentle, Balanced, Strong }
public enum CadenceQuality { Unknown, Clean, Poor }

public sealed record EnhancementIntent(EnhancementMode Mode, AutomaticGoal Goal, EnhancementStrength Strength,
    DetailIntent Detail, MotionIntent Motion, CleanupIntent Cleanup, PerformanceIntent Performance)
{
    public static EnhancementIntent ForAutomatic(AutomaticGoal goal = AutomaticGoal.BalancedImprovement,
        EnhancementStrength strength = EnhancementStrength.Normal, PerformanceIntent performance = PerformanceIntent.Balanced) =>
        new(EnhancementMode.Automatic, goal, strength, DetailIntent.Automatic, MotionIntent.Automatic,
            CleanupIntent.Automatic, performance);

    public static EnhancementIntent ForReference() =>
        new(EnhancementMode.Reference, AutomaticGoal.PreserveOriginal, EnhancementStrength.Subtle,
            DetailIntent.Preserve, MotionIntent.Original, CleanupIntent.PreserveTexture, PerformanceIntent.Efficient);

    public static EnhancementIntent ForEnhanced(DetailIntent detail = DetailIntent.Balanced,
        MotionIntent motion = MotionIntent.Original, CleanupIntent cleanup = CleanupIntent.Balanced,
        PerformanceIntent performance = PerformanceIntent.Balanced) =>
        new(EnhancementMode.Enhanced, AutomaticGoal.BalancedImprovement, EnhancementStrength.Normal,
            detail, motion, cleanup, performance);

    public static EnhancementIntent ForCompatibility() =>
        new(EnhancementMode.Compatibility, AutomaticGoal.PreserveOriginal, EnhancementStrength.Subtle,
            DetailIntent.Preserve, MotionIntent.Original, CleanupIntent.PreserveTexture, PerformanceIntent.Efficient);
}

public sealed record PlaybackEnvironment(MediaInfo Source, PlaybackTarget Target, PlaybackCapabilities Capabilities,
    int ItemCount = 1, bool GeneratedMotionAvailable = false, bool NeuralMotionAvailable = false);

public sealed class EnhancementDecision : IEquatable<EnhancementDecision>
{
    public EnhancementDecision(EnhancementIntent intent, DetailImplementation detail, MotionImplementation motion,
        CleanupImplementation cleanup, CadenceQuality cadence, double scale, ImmutableArray<string> reasons)
    {
        Intent = intent; Detail = detail; Motion = motion; Cleanup = cleanup; Cadence = cadence; Scale = scale; Reasons = reasons;
    }

    public EnhancementIntent Intent { get; }
    public DetailImplementation Detail { get; }
    public MotionImplementation Motion { get; }
    public CleanupImplementation Cleanup { get; }
    public CadenceQuality Cadence { get; }
    public double Scale { get; }
    public ImmutableArray<string> Reasons { get; }

    public PlaybackOptions ApplyTo(PlaybackOptions template) => template with
    {
        UpscaleMode = Detail switch { DetailImplementation.NvidiaVpp => "RtxVsr", DetailImplementation.Conventional => "HighQuality", _ => "Off" },
        MotionMode = Motion switch { MotionImplementation.CadenceCorrected => "Cadence", MotionImplementation.BlendSmooth => "Smooth", _ => "Off" },
        Cleanup = Cleanup != CleanupImplementation.Off,
        CleanupMode = Cleanup switch { CleanupImplementation.Gentle => "Gentle", CleanupImplementation.Balanced => "Normal", CleanupImplementation.Strong => "Strong", _ => "Off" },
        Intent = Intent,
    };

    public bool Equals(EnhancementDecision? other) => other is not null && Intent == other.Intent &&
        Detail == other.Detail && Motion == other.Motion && Cleanup == other.Cleanup && Cadence == other.Cadence &&
        Scale.Equals(other.Scale) && Reasons.SequenceEqual(other.Reasons);
    public override bool Equals(object? obj) => obj is EnhancementDecision other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Intent); hash.Add(Detail); hash.Add(Motion); hash.Add(Cleanup); hash.Add(Cadence); hash.Add(Scale);
        foreach (string reason in Reasons) hash.Add(reason, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
    public static bool operator ==(EnhancementDecision? left, EnhancementDecision? right) =>
        ReferenceEquals(left, right) || left is not null && left.Equals(right);
    public static bool operator !=(EnhancementDecision? left, EnhancementDecision? right) => !(left == right);
}

public static class EnhancementPreferences
{
    private static T Parse<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    public static EnhancementIntent IntentFor(string profile, string automaticGoal, string strength,
        string detail, string motion, string cleanup, string performance)
    {
        var mode = Parse(profile, EnhancementMode.Automatic);
        return mode switch
        {
            EnhancementMode.Reference => EnhancementIntent.ForReference(),
            EnhancementMode.Compatibility => EnhancementIntent.ForCompatibility(),
            EnhancementMode.Enhanced => EnhancementIntent.ForEnhanced(
                Parse(detail, DetailIntent.Balanced), Parse(motion, MotionIntent.Original),
                Parse(cleanup, CleanupIntent.Balanced), Parse(performance, PerformanceIntent.Balanced)),
            _ => EnhancementIntent.ForAutomatic(Parse(automaticGoal, AutomaticGoal.BalancedImprovement),
                Parse(strength, EnhancementStrength.Normal), Parse(performance, PerformanceIntent.Balanced)),
        };
    }
}

public static class EnhancementPlanner
{
    public static EnhancementDecision Decide(EnhancementIntent intent, PlaybackEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(environment);
        var reasons = ImmutableArray.CreateBuilder<string>();
        double scale = FitScale(environment.Source, environment.Target);
        CadenceQuality cadence = AssessCadence(environment.Source.Fps, environment.Target.RefreshRateHz);
        var dimensions = ResolveDimensions(intent);
        DetailImplementation detail = ResolveDetail(intent, dimensions.Detail, environment, scale, reasons);
        MotionImplementation motion = ResolveMotion(intent, dimensions.Motion, environment, cadence, reasons);
        CleanupImplementation cleanup = ResolveCleanup(intent, dimensions.Cleanup, environment, reasons);
        return new(intent, detail, motion, cleanup, cadence, scale, reasons.ToImmutable());
    }

    public static double FitScale(MediaInfo source, PlaybackTarget target)
    {
        if (!source.Known || target.Width <= 0 || target.Height <= 0) return 1;
        if (source.Aspect > 0 && Math.Abs(source.Aspect - (double)source.Width / source.Height) > 0.015) return 1;
        return Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height);
    }

    public static CadenceQuality AssessCadence(double sourceFps, double? displayRefreshHz)
    {
        if (sourceFps <= 0 || displayRefreshHz is not > 0 || double.IsNaN(sourceFps) ||
            double.IsNaN(displayRefreshHz.Value) || double.IsInfinity(sourceFps) || double.IsInfinity(displayRefreshHz.Value))
            return CadenceQuality.Unknown;
        double ratio = displayRefreshHz.Value / sourceFps;
        return Math.Abs(ratio - Math.Round(ratio)) <= 0.01 ? CadenceQuality.Clean : CadenceQuality.Poor;
    }

    private static (DetailIntent Detail, MotionIntent Motion, CleanupIntent Cleanup) ResolveDimensions(EnhancementIntent intent)
    {
        if (intent.Mode != EnhancementMode.Automatic) return (intent.Detail, intent.Motion, intent.Cleanup);
        return intent.Goal switch
        {
            AutomaticGoal.PreserveOriginal => (DetailIntent.Preserve, MotionIntent.Automatic, CleanupIntent.PreserveTexture),
            AutomaticGoal.ImproveDetail => (intent.Strength switch
            {
                EnhancementStrength.Subtle => DetailIntent.Balanced,
                EnhancementStrength.Normal => DetailIntent.Sharper,
                _ => DetailIntent.Maximum,
            }, MotionIntent.Automatic, CleanupIntent.PreserveTexture),
            AutomaticGoal.SmootherMotion => (DetailIntent.Preserve, intent.Strength switch
            {
                EnhancementStrength.Subtle => MotionIntent.CadenceCorrected,
                EnhancementStrength.Normal => MotionIntent.BlendSmooth,
                _ => MotionIntent.GeneratedMotion,
            }, CleanupIntent.PreserveTexture),
            AutomaticGoal.CleanImage => (DetailIntent.Preserve, MotionIntent.Automatic,
                intent.Strength == EnhancementStrength.Strong ? CleanupIntent.Clean : CleanupIntent.Balanced),
            _ => (intent.Strength == EnhancementStrength.Subtle ? DetailIntent.Balanced :
                    intent.Strength == EnhancementStrength.Normal ? DetailIntent.Sharper : DetailIntent.Maximum,
                intent.Strength == EnhancementStrength.Subtle ? MotionIntent.CadenceCorrected :
                    intent.Strength == EnhancementStrength.Normal ? MotionIntent.BlendSmooth : MotionIntent.GeneratedMotion,
                intent.Strength == EnhancementStrength.Strong ? CleanupIntent.Clean : CleanupIntent.Balanced),
        };
    }

    private static DetailImplementation ResolveDetail(EnhancementIntent intent, DetailIntent requested,
        PlaybackEnvironment environment, double scale, ImmutableArray<string>.Builder reasons)
    {
        if (intent.Mode is EnhancementMode.Reference or EnhancementMode.Compatibility || requested == DetailIntent.Preserve)
        {
            reasons.Add(intent.Mode == EnhancementMode.Reference
                ? "Source detail preserved because Reference playback minimizes discretionary processing."
                : "No discretionary detail enhancement was requested.");
            return DetailImplementation.None;
        }
        if (!environment.Source.Known || environment.Target.Width <= 0 || environment.Target.Height <= 0)
        {
            reasons.Add("Conventional source presentation selected because source or output dimensions are unknown.");
            return DetailImplementation.None;
        }
        if (scale <= 1.001)
        {
            reasons.Add("No discretionary upscaling selected because the source already meets or exceeds the output size.");
            return DetailImplementation.None;
        }
        if (intent.Performance == PerformanceIntent.Efficient && scale < 1.2)
        {
            reasons.Add("Optional detail enhancement was skipped because the upscale is marginal and Efficient performance was requested.");
            return DetailImplementation.None;
        }
        bool nvidiaEligible = environment.Capabilities.Nvidia && environment.Capabilities.Rtx &&
            environment.Capabilities.Vpp && scale <= 8 && intent.Mode != EnhancementMode.Compatibility;
        if (requested == DetailIntent.Maximum && intent.Performance == PerformanceIntent.MaximumQuality && nvidiaEligible)
        {
            reasons.Add("RTX Super Resolution selected because the source is below output resolution and compatible NVIDIA processing is available.");
            return DetailImplementation.NvidiaVpp;
        }
        if (requested == DetailIntent.Maximum && intent.Performance == PerformanceIntent.MaximumQuality && !nvidiaEligible)
            reasons.Add("High-quality conventional scaling selected because compatible NVIDIA processing is not available for this playback path.");
        else
            reasons.Add("High-quality conventional scaling selected for the requested detail and performance preference.");
        return DetailImplementation.Conventional;
    }

    private static MotionImplementation ResolveMotion(EnhancementIntent intent, MotionIntent requested,
        PlaybackEnvironment environment, CadenceQuality cadence, ImmutableArray<string>.Builder reasons)
    {
        if (intent.Mode == EnhancementMode.Compatibility)
        {
            reasons.Add("Source cadence preserved on the Compatibility playback path.");
            return MotionImplementation.Original;
        }
        if (intent.Mode == EnhancementMode.Reference || requested == MotionIntent.Automatic)
        {
            if (cadence == CadenceQuality.Poor)
            {
                reasons.Add("Cadence-corrected presentation selected because the source does not map cleanly to the measured display refresh.");
                return MotionImplementation.CadenceCorrected;
            }
            if (cadence == CadenceQuality.Unknown)
                reasons.Add("Source cadence preserved conservatively because the display refresh rate is unknown.");
            else
                reasons.Add("Source cadence preserved because it maps cleanly to the measured display refresh and cinematic motion preservation was requested.");
            return MotionImplementation.Original;
        }
        if (requested == MotionIntent.Original)
        {
            reasons.Add("Original temporal samples preserved because motion enhancement was not requested.");
            return MotionImplementation.Original;
        }
        if (requested == MotionIntent.CadenceCorrected)
        {
            if (cadence == CadenceQuality.Poor)
            {
                reasons.Add("Cadence-corrected presentation selected because the source does not map cleanly to the measured display refresh.");
                return MotionImplementation.CadenceCorrected;
            }
            reasons.Add(cadence == CadenceQuality.Clean
                ? "Source cadence preserved because it already maps cleanly to the measured display refresh."
                : "Source cadence preserved conservatively because the display refresh rate is unknown.");
            return MotionImplementation.Original;
        }
        if (requested == MotionIntent.NeuralMotion && environment.NeuralMotionAvailable)
        {
            reasons.Add("Neural motion selected because that capability is available for this playback path.");
            return MotionImplementation.NeuralMotion;
        }
        if (requested is MotionIntent.NeuralMotion or MotionIntent.GeneratedMotion && environment.GeneratedMotionAvailable)
        {
            reasons.Add("Generated motion selected because motion-compensated interpolation is available for this playback path.");
            return MotionImplementation.GeneratedMotion;
        }
        if (requested == MotionIntent.NeuralMotion)
            reasons.Add("Temporal blend smoothing selected because neural motion is not available in the current playback path.");
        else if (requested == MotionIntent.GeneratedMotion)
            reasons.Add("Temporal blend smoothing selected because generated-frame interpolation is not available in the current playback path.");
        else
            reasons.Add("Temporal blend smoothing selected because smoother motion was requested; this blends neighbouring source frames and is not frame generation.");
        return MotionImplementation.BlendSmooth;
    }

    private static CleanupImplementation ResolveCleanup(EnhancementIntent intent, CleanupIntent requested,
        PlaybackEnvironment environment, ImmutableArray<string>.Builder reasons)
    {
        if (intent.Mode is EnhancementMode.Reference or EnhancementMode.Compatibility || requested == CleanupIntent.PreserveTexture)
        {
            reasons.Add("Texture-preserving cleanup selected; discretionary debanding is off.");
            return CleanupImplementation.Off;
        }
        if (requested == CleanupIntent.Automatic)
        {
            bool highBitDepth = environment.Source.PixelFormat.Contains("p10", StringComparison.OrdinalIgnoreCase) ||
                environment.Source.PixelFormat.Contains("p12", StringComparison.OrdinalIgnoreCase);
            if (environment.ItemCount != 1 || !environment.Source.Known || environment.Source.IsHdr || highBitDepth)
            {
                reasons.Add("Automatic cleanup stayed off because the available source facts do not justify discretionary debanding.");
                return CleanupImplementation.Off;
            }
            reasons.Add("Gentle cleanup selected conservatively for a single known 8-bit SDR source; banding and grain were not inferred.");
            return CleanupImplementation.Gentle;
        }
        if (requested == CleanupIntent.Clean)
        {
            var selected = intent.Performance == PerformanceIntent.Efficient ? CleanupImplementation.Balanced : CleanupImplementation.Strong;
            reasons.Add(selected == CleanupImplementation.Strong
                ? "Strong cleanup selected from the user's Clean enhancement preference."
                : "Balanced cleanup selected because Clean was requested with Efficient performance.");
            return selected;
        }
        var balanced = intent.Performance == PerformanceIntent.Efficient ? CleanupImplementation.Gentle : CleanupImplementation.Balanced;
        reasons.Add((balanced == CleanupImplementation.Gentle ? "Gentle" : "Balanced") +
            " cleanup selected from the user's Balanced enhancement preference.");
        return balanced;
    }
}
