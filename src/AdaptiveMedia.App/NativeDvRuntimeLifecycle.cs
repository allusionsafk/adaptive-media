using System.Text.Json;

namespace AdaptiveMedia;

/// <summary>What the runtime lifecycle is doing, in terms a person can act on.
/// Stable diagnostics identifiers; append rather than renumber.</summary>
public enum NativeDvLifecycleState
{
    NotInstalled = 0,
    Ready = 1,
    Updated = 2,
    UpdateAvailable = 3,
    UpdateFailedPreviousRetained = 4,
    VerificationFailed = 5,
    UnsupportedRuntime = 6,
    ProvisioningNotAllowed = 7,
    Busy = 8,
    DescriptorUnavailable = 9,
}

/// <summary>Which generation is in use and which is kept as a fallback.</summary>
public sealed record NativeDvLifecycleRecord(string? Current, string? Previous, string? UpdatedAtUtc)
{
    public static NativeDvLifecycleRecord Empty { get; } = new(null, null, null);
}

public sealed record NativeDvLifecycleStatus(
    NativeDvLifecycleState State,
    NativeDvRuntime? Runtime,
    string? CurrentGeneration,
    string? PreviousGeneration,
    int GenerationsRemoved,
    string? Reason)
{
    public bool IsReady => State is NativeDvLifecycleState.Ready or NativeDvLifecycleState.Updated && Runtime is not null;

    /// <summary>One line for the developer setting and diagnostics. It never claims
    /// more than the lifecycle actually established.</summary>
    public string Summary => State switch
    {
        NativeDvLifecycleState.Ready => "Native Dolby Vision runtime ready.",
        NativeDvLifecycleState.Updated => "Native Dolby Vision runtime updated; the previous one is kept as a fallback.",
        NativeDvLifecycleState.UpdateAvailable => "A newer native Dolby Vision runtime is available.",
        NativeDvLifecycleState.UpdateFailedPreviousRetained => "The native Dolby Vision runtime update failed; the previous runtime is still in place.",
        NativeDvLifecycleState.VerificationFailed => "The native Dolby Vision runtime failed verification and was not installed.",
        NativeDvLifecycleState.UnsupportedRuntime => "This native Dolby Vision runtime is not one this build knows how to read; playback would not be able to report what it composed.",
        NativeDvLifecycleState.ProvisioningNotAllowed => "The native Dolby Vision runtime is not installed and automatic download is turned off.",
        NativeDvLifecycleState.Busy => "Another Adaptive Media process is installing the native Dolby Vision runtime.",
        NativeDvLifecycleState.DescriptorUnavailable => "This build does not describe a native Dolby Vision runtime.",
        _ => "The native Dolby Vision runtime is not installed.",
    };
}

/// <summary>Whether the retained previous generation may be used as a playback
/// fallback, and if not, why not. Stable diagnostics identifiers; append rather
/// than renumber.</summary>
public enum NativeDvFallbackState
{
    Available = 0,
    NoPreviousGeneration = 1,
    /// <summary>The generation is on disk but its own pinned manifest was never
    /// recorded, so nothing can verify it before it is launched.</summary>
    ManifestUnavailable = 2,
    /// <summary>The generation no longer matches the manifest that admitted it.</summary>
    StructurallyInvalid = 3,
    /// <summary>No verified diagnostic adapter covers this build.</summary>
    AdapterUnsupported = 4,
    /// <summary>Already tried during this playback attempt.</summary>
    AlreadyAttempted = 5,
    /// <summary>Already failed earlier in this session.</summary>
    KnownUnhealthy = 6,
}

/// <summary>A retained generation considered as a playback fallback. A runtime is
/// only ever present here when it validated in full.</summary>
public sealed record NativeDvFallbackCandidate(
    NativeDvFallbackState State,
    string? GenerationId,
    NativeDvRuntimeDescriptor? Descriptor,
    NativeDvRuntime? Runtime,
    string Reason)
{
    public bool IsUsable => State == NativeDvFallbackState.Available && Runtime is not null && Descriptor is not null;
}

/// <summary>Versioned lifecycle over the content-addressed runtime store.
///
/// The store installs and validates one generation. This adds the part a product
/// needs: knowing which generation is current, keeping the one it replaced as a
/// fallback, removing only generations that are genuinely superseded, surviving a
/// failed or interrupted update with the working runtime intact, and keeping two
/// processes from tripping over each other.
///
/// Nothing here mutates an installed generation, and nothing outside the
/// application-owned root is ever written or deleted.</summary>
public sealed class NativeDvRuntimeLifecycle
{
    public const string StateFileName = "state.json";
    public const string LockFileName = ".lock";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly NativeDvRuntimeStore _store;

    public NativeDvRuntimeLifecycle(NativeDvRuntimeStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public NativeDvRuntimeStore Store => _store;
    public string StatePath => Path.Combine(_store.RootPath, StateFileName);

    /// <summary>A generation id is the archive SHA-256 and nothing else. Anything
    /// that is not exactly that is never treated as a generation, so a malformed or
    /// hostile state file cannot name a directory to act on.
    ///
    /// The rule itself lives in the store, which is the layer that names
    /// directories; this defers to it so the two cannot drift apart.</summary>
    public static bool IsGenerationId(string? value) => NativeDvRuntimeStore.IsGenerationIdentifier(value);

    public NativeDvLifecycleRecord ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return NativeDvLifecycleRecord.Empty;
            var record = JsonSerializer.Deserialize<NativeDvLifecycleRecord>(File.ReadAllText(StatePath));
            if (record is null) return NativeDvLifecycleRecord.Empty;
            // The file is advisory. Only well-formed ids survive reading it.
            return new(IsGenerationId(record.Current) ? record.Current : null,
                       IsGenerationId(record.Previous) ? record.Previous : null,
                       record.UpdatedAtUtc);
        }
        catch (IOException) { return NativeDvLifecycleRecord.Empty; }
        catch (JsonException) { return NativeDvLifecycleRecord.Empty; }
        catch (UnauthorizedAccessException) { return NativeDvLifecycleRecord.Empty; }
    }

    private void WriteState(NativeDvLifecycleRecord record)
    {
        Directory.CreateDirectory(_store.RootPath);
        string temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
        File.Move(temporary, StatePath, overwrite: true);
    }

    /// <summary>Exclusive cross-process coordination for anything that installs,
    /// promotes, or deletes. Resolving is read-only and deliberately does not take
    /// it, so a busy installer never blocks playback from using what already works.</summary>
    public IDisposable? TryAcquireLock()
    {
        try
        {
            Directory.CreateDirectory(_store.RootPath);
            return new FileStream(Path.Combine(_store.RootPath, LockFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Bring the store into line with the shipped descriptor, then tidy up.
    ///
    /// Repeating this converges: an already-current, valid generation is not
    /// downloaded again, state is not rewritten when it already agrees, and cleanup
    /// has nothing left to remove.</summary>
    public async Task<NativeDvLifecycleStatus> ReconcileAsync(NativeDvRuntimeDescriptor? descriptor,
        bool allowDownload, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        if (descriptor is null)
            return new(NativeDvLifecycleState.DescriptorUnavailable, null, null, null, 0,
                "This build does not describe a native Dolby Vision runtime.");

        // Pre-flight: refuse to install a runtime this build cannot read.
        //
        // This asks a different question from the observation adapter, and the
        // distinction matters. Here the descriptor's commit is the right input
        // because the question is "should we install this?". Observation still
        // decides support from the runtime that actually ran, so this gate cannot
        // become a way to claim composition on an unverified build.
        if (!NativeDvDiagnosticAdapters.SupportsCommit(descriptor.MpvCommit))
        {
            var known = ReadState();
            return new(NativeDvLifecycleState.UnsupportedRuntime, null, known.Current, known.Previous, 0,
                $"This build has no verified way to read diagnostics from mpv {descriptor.MpvCommit}, so it could not report what playback composed.");
        }

        using var lockHandle = TryAcquireLock();
        if (lockHandle is null)
        {
            // Another process owns installation. If what is already on disk works,
            // say so plainly rather than reporting a problem.
            var concurrent = _store.Resolve(descriptor);
            var busyState = ReadState();
            return concurrent.IsUsable
                ? new(NativeDvLifecycleState.Ready, concurrent.Runtime, descriptor.VersionId, busyState.Previous, 0, null)
                : new(NativeDvLifecycleState.Busy, null, busyState.Current, busyState.Previous, 0,
                    "Another Adaptive Media process is installing the native Dolby Vision runtime.");
        }

        var prior = ReadState();
        bool wasAnUpdate = prior.Current is not null && prior.Current != descriptor.VersionId;

        var resolution = _store.Resolve(descriptor);
        bool provisioned = false;
        if (!resolution.IsUsable)
        {
            resolution = await _store.ProvisionAsync(descriptor, allowDownload, progress, cancellation);
            provisioned = true;
        }

        if (!resolution.IsUsable)
        {
            // Nothing was deleted and nothing was demoted, so whatever worked before
            // still works. Say which case this is.
            bool retained = prior.Current is not null && GenerationExists(prior.Current);
            var failure = resolution.State switch
            {
                NativeDvRuntimeState.ProvisioningNotAllowed => NativeDvLifecycleState.ProvisioningNotAllowed,
                NativeDvRuntimeState.UnsupportedRuntime => NativeDvLifecycleState.UnsupportedRuntime,
                NativeDvRuntimeState.NotInstalled => NativeDvLifecycleState.NotInstalled,
                _ => retained ? NativeDvLifecycleState.UpdateFailedPreviousRetained : NativeDvLifecycleState.VerificationFailed,
            };
            return new(failure, null, prior.Current, prior.Previous, 0, resolution.FailureReason);
        }

        // The candidate is installed and validated. Only now does the outgoing
        // generation become the retained fallback.
        var next = prior.Current == descriptor.VersionId
            ? prior
            : new NativeDvLifecycleRecord(descriptor.VersionId, prior.Current, DateTimeOffset.UtcNow.ToString("o"));
        if (!ReferenceEquals(next, prior)) WriteState(next);

        // Record this generation's own pinned manifest while we still have it.
        // Once it has been superseded the shipped descriptor describes a different
        // build, and without this there would be nothing to validate it against if
        // playback ever had to fall back to it. Writing it every time is
        // deliberate: it also backfills a generation installed before this existed,
        // as soon as that generation is reconciled again.
        _store.WriteDescriptorSnapshot(descriptor);

        int removed = CollectGarbage(descriptor, next);
        var state = wasAnUpdate && provisioned ? NativeDvLifecycleState.Updated : NativeDvLifecycleState.Ready;
        return new(state, resolution.Runtime, next.Current, next.Previous, removed, null);
    }

    private bool GenerationExists(string generationId) =>
        IsGenerationId(generationId) && Directory.Exists(Path.Combine(_store.RootPath, generationId));

    /// <summary>Delete only generations that are provably superseded.
    ///
    /// Every candidate must be a direct child of the application-owned root whose
    /// name is a generation id, and must be none of current, previous, or the
    /// descriptor's own generation. A directory that is in use simply stays; it is
    /// never forced. No path taken from a manifest or state file is ever recursively
    /// deleted on trust.</summary>
    public int CollectGarbage(NativeDvRuntimeDescriptor? descriptor, NativeDvLifecycleRecord? record = null)
    {
        if (!Directory.Exists(_store.RootPath)) return 0;
        record ??= ReadState();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (descriptor is not null) keep.Add(descriptor.VersionId);
        if (record.Current is not null) keep.Add(record.Current);
        if (record.Previous is not null) keep.Add(record.Previous);

        string root = Path.GetFullPath(_store.RootPath);
        int removed = 0;
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (!IsGenerationId(name)) continue;           // staging and anything unexpected is left alone
            if (keep.Contains(name)) continue;
            // In use by this or another process: excluded before any deletion is
            // attempted. Letting the delete run and fail would strip whatever it
            // reached first and leave a gutted directory behind.
            if (_store.IsPinned(name)) continue;
            // Re-derive the path from the validated name rather than trusting the
            // enumerated string, and require it to sit directly under the root.
            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(target, recursive: true); removed++; }
            catch (IOException) { }                        // in use: leave it for next time
            catch (UnauthorizedAccessException) { }
        }
        // Recorded manifests follow the generations they describe, so a manifest is
        // kept for anything still on disk as well as anything deliberately
        // retained. A generation that survived because it was in use must keep its
        // manifest, or it would stop being validatable as a fallback. This is not
        // counted in the return value: removing a manifest does not remove a
        // runtime.
        var manifestsToKeep = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (IsGenerationId(name)) manifestsToKeep.Add(name);
        }
        _store.CleanupDescriptorSnapshots(manifestsToKeep);
        return removed;
    }

    /// <summary>Remove staging left behind by an interrupted install, under the same
    /// root check. Promoted generations are never touched.</summary>
    public int CleanupStaging() => _store.CleanupStaging();

    /// <summary>Decide whether the retained previous generation may be used as a
    /// playback fallback, and if not, exactly why.
    ///
    /// Every gate here is a refusal to launch something unproven. The retained
    /// generation is validated by the same full component-hash check that admitted
    /// it in the first place, and its diagnostic adapter is checked before that, so
    /// a fallback can never be a runtime this build would be unable to read. The
    /// generation that just failed, anything already tried in this attempt, and
    /// anything already known to have failed this session are all excluded, which
    /// is what makes a rollback unable to bounce.
    ///
    /// This reports; it does not promote. Nothing here changes which generation is
    /// current, and nothing here writes to the store.</summary>
    public NativeDvFallbackCandidate ResolveFallback(string? failedGenerationId,
        IReadOnlyCollection<string>? alreadyAttempted = null, NativeDvHealthMemory? health = null)
    {
        string? previous = ReadState().Previous;
        if (!IsGenerationId(previous))
            return new(NativeDvFallbackState.NoPreviousGeneration, null, null, null,
                "No previous native Dolby Vision runtime is retained.");

        if (IsGenerationId(failedGenerationId) &&
            previous!.Equals(failedGenerationId, StringComparison.OrdinalIgnoreCase))
            return new(NativeDvFallbackState.NoPreviousGeneration, previous, null, null,
                "The retained runtime is the one that just failed, so there is nothing to fall back to.");

        if (alreadyAttempted is not null &&
            alreadyAttempted.Any(x => previous!.Equals(x, StringComparison.OrdinalIgnoreCase)))
            return new(NativeDvFallbackState.AlreadyAttempted, previous, null, null,
                "The retained native Dolby Vision runtime was already tried for this playback.");

        if (health is not null && health.IsKnownUnhealthy(previous))
            return new(NativeDvFallbackState.KnownUnhealthy, previous, null, null,
                "The retained native Dolby Vision runtime already failed earlier in this session.");

        // Without its own pinned manifest there is nothing to validate the tree
        // against, and launching it on the strength of its directory name alone
        // would defeat the point of a content-addressed store.
        var descriptor = _store.ReadDescriptorSnapshot(previous);
        if (descriptor is null)
            return new(NativeDvFallbackState.ManifestUnavailable, previous, null, null,
                "The retained native Dolby Vision runtime has no recorded manifest, so it cannot be verified before use.");

        // Asked before hashing: a runtime this build cannot read would be refused
        // anyway, and validating it first would mean hashing the whole runtime to
        // reach the same answer.
        if (!NativeDvDiagnosticAdapters.SupportsCommit(descriptor.MpvCommit))
            return new(NativeDvFallbackState.AdapterUnsupported, previous, descriptor, null,
                $"This build has no verified way to read diagnostics from mpv {descriptor.MpvCommit}, so falling back to it could not report what playback composed.");

        var resolution = _store.Resolve(descriptor);
        if (!resolution.IsUsable)
            return new(NativeDvFallbackState.StructurallyInvalid, previous, descriptor, null,
                resolution.FailureReason ?? "The retained native Dolby Vision runtime no longer validates.");

        return new(NativeDvFallbackState.Available, previous, descriptor, resolution.Runtime,
            $"The retained native Dolby Vision runtime {descriptor.MpvVersion} validated and can be used as a fallback.");
    }
}
