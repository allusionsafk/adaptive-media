using System.Collections.Immutable;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdaptiveMedia;

/// <summary>Why a native Dolby Vision runtime is or is not usable. Names and
/// values are stable diagnostics identifiers; append rather than renumber.</summary>
public enum NativeDvRuntimeState
{
    NotInstalled = 0,
    Installed = 1,
    Incomplete = 2,
    ComponentHashMismatch = 3,
    ArchiveHashMismatch = 4,
    DownloadUnavailable = 5,
    ExtractionFailed = 6,
    PromotionFailed = 7,
    Cancelled = 8,
    ProvisioningNotAllowed = 9,
    DescriptorUnavailable = 10,
    UnsupportedRuntime = 11,
}

/// <summary>One file the runtime cannot work without, pinned by exact size and hash.
/// Archive names are never trusted; only content is.</summary>
public sealed record NativeDvRuntimeComponent(string RelativePath, string Sha256, long Bytes);

/// <summary>The pinned identity, provenance, and contents of one native Dolby
/// Vision runtime generation.</summary>
public sealed record NativeDvRuntimeDescriptor(
    string VersionId,
    string MpvVersion,
    string MpvCommit,
    int LibplaceboApi,
    string Provider,
    string ReleaseUrl,
    Uri ArchiveUrl,
    string ArchiveSha256,
    long ArchiveBytes,
    string LauncherRelativePath,
    ImmutableArray<NativeDvRuntimeComponent> Components)
{
    /// <summary>Parse the pinned runtime manifest. The application ships the very
    /// same file the experiment pinned, so the two cannot drift apart.</summary>
    public static NativeDvRuntimeDescriptor FromManifestJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
            throw new InvalidDataException("Unsupported native Dolby Vision runtime manifest schema.");

        var archive = root.GetProperty("archive");
        var runtime = root.GetProperty("runtime");
        var mpv = root.GetProperty("mpv");
        var placebo = root.GetProperty("libplacebo");
        var provider = root.GetProperty("provider");

        string archiveSha = RequireSha256(archive.GetProperty("sha256").GetString(), "archive");
        var components = ImmutableArray.CreateBuilder<NativeDvRuntimeComponent>();
        string? launcher = null;
        foreach (string name in new[] { "executable", "consoleLauncher" })
        {
            if (!runtime.TryGetProperty(name, out var entry)) continue;
            string relative = ToArchiveRelativePath(entry.GetProperty("pathRelativeToManifest").GetString());
            var component = new NativeDvRuntimeComponent(relative,
                RequireSha256(entry.GetProperty("sha256").GetString(), name), entry.GetProperty("bytes").GetInt64());
            components.Add(component);
            // The product launches the windowed executable; the stable lane does the
            // same, and the console wrapper would attach a console to a GUI app.
            if (name == "executable") launcher = relative;
        }
        if (components.Count == 0) throw new InvalidDataException("The runtime manifest names no components.");
        launcher ??= components[0].RelativePath;

        return new NativeDvRuntimeDescriptor(
            VersionId: archiveSha,
            MpvVersion: mpv.GetProperty("version").GetString() ?? "unknown",
            MpvCommit: mpv.GetProperty("commit").GetString() ?? "unknown",
            LibplaceboApi: placebo.GetProperty("apiVersion").GetInt32(),
            Provider: provider.GetProperty("name").GetString() ?? "unknown",
            ReleaseUrl: provider.GetProperty("releaseUrl").GetString() ?? "",
            ArchiveUrl: new Uri(archive.GetProperty("url").GetString() ?? throw new InvalidDataException("The manifest names no archive URL.")),
            ArchiveSha256: archiveSha,
            ArchiveBytes: archive.GetProperty("bytes").GetInt64(),
            LauncherRelativePath: launcher,
            Components: components.ToImmutable());
    }

    private static string RequireSha256(string? value, string what)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException($"The runtime manifest does not pin a SHA-256 for {what}.");
        return value.ToLowerInvariant();
    }

    // Manifest paths are expressed relative to the manifest and point inside the
    // extracted tree. Keep whatever follows that marker so nested layouts survive.
    private static string ToArchiveRelativePath(string? manifestRelative)
    {
        if (string.IsNullOrWhiteSpace(manifestRelative)) throw new InvalidDataException("A runtime component has no path.");
        string normalized = manifestRelative.Replace('\\', '/');
        const string marker = "/extracted/";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        string relative = index >= 0 ? normalized[(index + marker.Length)..] : Path.GetFileName(normalized);
        if (relative.Length == 0) throw new InvalidDataException("A runtime component path is empty.");
        return relative.Replace('/', Path.DirectorySeparatorChar);
    }
}

/// <summary>The outcome of resolving or provisioning a runtime. A runtime is only
/// present when every pinned component validated.</summary>
public sealed record NativeDvRuntimeResolution(
    NativeDvRuntimeState State,
    NativeDvRuntime? Runtime,
    string? InstalledPath,
    string? FailureReason)
{
    public bool IsUsable => State == NativeDvRuntimeState.Installed && Runtime is not null;
}

public delegate Task NativeDvArchiveFetch(Uri source, string destinationPath, CancellationToken cancellation);
public delegate Task NativeDvArchiveExtract(string archivePath, string destinationDirectory, CancellationToken cancellation);

/// <summary>An application-owned, content-addressed store of native Dolby Vision
/// runtimes.
///
/// It never installs into a shared location, never touches the stable runtime,
/// PATH, or file associations, and never exposes a partially provisioned tree:
/// work happens in a staging generation that is validated in full and only then
/// moved into place as a single filesystem operation.</summary>
public sealed class NativeDvRuntimeStore
{
    public const string StagingDirectoryName = ".staging";

    /// <summary>Where a generation's own pinned manifest is kept. It sits beside
    /// the generations rather than inside one, so recording it never writes into
    /// an installed tree and an installed generation stays immutable.</summary>
    public const string DescriptorDirectoryName = ".descriptors";

    /// <summary>Where in-use markers live. Outside the generations, and not named
    /// like one, so cleanup never mistakes it for a runtime.</summary>
    public const string PinDirectoryName = ".pins";

    private readonly NativeDvArchiveFetch _fetch;
    private readonly NativeDvArchiveExtract _extract;

    public NativeDvRuntimeStore(string rootPath, NativeDvArchiveFetch? fetch = null, NativeDvArchiveExtract? extract = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("A runtime root is required.", nameof(rootPath));
        RootPath = Path.GetFullPath(rootPath);
        _fetch = fetch ?? DownloadAsync;
        _extract = extract ?? ExtractAsync;
    }

    public string RootPath { get; }

    /// <summary>The default application-owned location. It sits under the same
    /// per-user data directory the rest of the application already writes to, so
    /// nothing is installed machine-wide.</summary>
    public static string DefaultRootPath => Path.Combine(SettingsStore.DirectoryPath, "runtimes", "native-dv");

    public string GenerationPath(NativeDvRuntimeDescriptor descriptor) => Path.Combine(RootPath, descriptor.VersionId);

    /// <summary>Validate an installed generation without changing anything. This is
    /// the already-installed fast path and the reuse check; it is deliberately the
    /// only thing that can report a runtime as usable.</summary>
    public NativeDvRuntimeResolution Resolve(NativeDvRuntimeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string generation = GenerationPath(descriptor);
        if (!Directory.Exists(generation))
            return new(NativeDvRuntimeState.NotInstalled, null, null, "The native Dolby Vision runtime is not installed.");
        return ValidateTree(descriptor, generation);
    }

    private NativeDvRuntimeResolution ValidateTree(NativeDvRuntimeDescriptor descriptor, string tree)
    {
        foreach (var component in descriptor.Components)
        {
            string path = ResolveInside(tree, component.RelativePath);
            var file = new FileInfo(path);
            if (!file.Exists)
                return new(NativeDvRuntimeState.Incomplete, null, tree,
                    $"The runtime is missing {component.RelativePath}.");
            if (file.Length != component.Bytes)
                return new(NativeDvRuntimeState.Incomplete, null, tree,
                    $"The runtime file {component.RelativePath} is {file.Length} bytes; {component.Bytes} were expected.");
            if (!string.Equals(ComputeSha256(path), component.Sha256, StringComparison.OrdinalIgnoreCase))
                return new(NativeDvRuntimeState.ComponentHashMismatch, null, tree,
                    $"The runtime file {component.RelativePath} does not match its pinned SHA-256.");
        }

        string launcher = ResolveInside(tree, descriptor.LauncherRelativePath);
        var runtime = new NativeDvRuntime(launcher,
            descriptor.Components.First(x => x.RelativePath == descriptor.LauncherRelativePath).Sha256,
            descriptor.MpvVersion, descriptor.LibplaceboApi);
        if (!runtime.ComposesEnhancementLayer)
            return new(NativeDvRuntimeState.UnsupportedRuntime, null, tree,
                $"The runtime reports libplacebo API {descriptor.LibplaceboApi}; composition requires at least {NativeDvRuntime.RequiredLibplaceboApi}.");
        return new(NativeDvRuntimeState.Installed, runtime, tree, null);
    }

    /// <summary>Reject any component path that would escape the generation directory,
    /// so a manifest or archive cannot write outside the store.</summary>
    private static string ResolveInside(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The runtime component path escapes the runtime directory: {relative}");
        return full;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Resolve, and provision only if needed and permitted.
    /// Download and extraction happen inside a staging generation that is validated
    /// in full before it is promoted.</summary>
    public async Task<NativeDvRuntimeResolution> ProvisionAsync(NativeDvRuntimeDescriptor descriptor,
        bool allowDownload, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var existing = Resolve(descriptor);
        if (existing.IsUsable) return existing;

        if (!allowDownload)
            return new(NativeDvRuntimeState.ProvisioningNotAllowed, null, null,
                "The native Dolby Vision runtime is not installed and automatic download is turned off.");

        string staging = Path.Combine(RootPath, StagingDirectoryName, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            string archive = Path.Combine(staging, "runtime-archive");
            string tree = Path.Combine(staging, "tree");

            progress?.Report("Downloading the native Dolby Vision runtime…");
            try { await _fetch(descriptor.ArchiveUrl, archive, cancellation); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                return new(NativeDvRuntimeState.DownloadUnavailable, null, null,
                    "The native Dolby Vision runtime could not be downloaded.");
            }
            cancellation.ThrowIfCancellationRequested();

            // Verify before extracting. An archive that fails here is never opened.
            if (!File.Exists(archive))
                return new(NativeDvRuntimeState.DownloadUnavailable, null, null, "The downloaded runtime archive is missing.");
            var archiveFile = new FileInfo(archive);
            if (archiveFile.Length != descriptor.ArchiveBytes)
                return new(NativeDvRuntimeState.ArchiveHashMismatch, null, null,
                    $"The runtime archive is {archiveFile.Length} bytes; {descriptor.ArchiveBytes} were expected.");
            if (!string.Equals(ComputeSha256(archive), descriptor.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                return new(NativeDvRuntimeState.ArchiveHashMismatch, null, null,
                    "The runtime archive does not match its pinned SHA-256.");

            progress?.Report("Installing the native Dolby Vision runtime…");
            Directory.CreateDirectory(tree);
            try { await _extract(archive, tree, cancellation); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return new(NativeDvRuntimeState.ExtractionFailed, null, null,
                    "The native Dolby Vision runtime archive could not be extracted.");
            }
            cancellation.ThrowIfCancellationRequested();

            // Validate the staged tree exactly as a reused one would be validated, so
            // an incomplete or tampered extraction can never be promoted.
            var staged = ValidateTree(descriptor, tree);
            if (staged.State != NativeDvRuntimeState.Installed)
                return staged with { InstalledPath = null };

            string generation = GenerationPath(descriptor);
            Directory.CreateDirectory(RootPath);
            try
            {
                Directory.Move(tree, generation);
            }
            catch (IOException)
            {
                // Another instance may have promoted the same generation first. That
                // is success if what landed validates, and a failure otherwise.
                var raced = Resolve(descriptor);
                if (raced.IsUsable) return raced;
                return new(NativeDvRuntimeState.PromotionFailed, null, null,
                    "The native Dolby Vision runtime could not be moved into place.");
            }
            return Resolve(descriptor);
        }
        catch (OperationCanceledException)
        {
            return new(NativeDvRuntimeState.Cancelled, null, null, "Runtime installation was cancelled.");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Record a generation's own pinned manifest so it can be validated
    /// later without the shipped descriptor.
    ///
    /// A retained previous generation is only a directory of files; on its own
    /// there is nothing to check it against, because the shipped descriptor pins
    /// the hashes of a different build. Keeping each generation's manifest means a
    /// rollback candidate is verified by exactly the code that verified it when it
    /// was installed, rather than being launched on trust.
    ///
    /// The snapshot is written in the pinned manifest schema on purpose: there is
    /// then one parser and one validator for both, and no second format to keep in
    /// step. Best-effort, because failing to record this must never fail an
    /// install that otherwise succeeded.</summary>
    public bool WriteDescriptorSnapshot(NativeDvRuntimeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string? json = ToManifestJson(descriptor);
        if (json is null) return false;
        try
        {
            string path = DescriptorSnapshotPath(descriptor.VersionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Re-read the snapshot through the ordinary parser before it counts as
            // written, so a snapshot that could not be read back is never left
            // behind to be trusted later.
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            var reparsed = NativeDvRuntimeDescriptor.FromManifestJson(File.ReadAllText(temporary));
            if (reparsed.VersionId != descriptor.VersionId ||
                reparsed.LauncherRelativePath != descriptor.LauncherRelativePath ||
                reparsed.Components.Length != descriptor.Components.Length)
            {
                File.Delete(temporary);
                return false;
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (JsonException) { return false; }
    }

    public string DescriptorSnapshotPath(string generationId) =>
        Path.Combine(RootPath, DescriptorDirectoryName, generationId + ".json");

    /// <summary>The recorded manifest for a generation, or null when there is none
    /// or it does not describe that generation.
    ///
    /// The identifier must be a generation id and the parsed manifest must pin the
    /// very generation asked for, so a stray or edited file cannot be used to
    /// describe a different build.</summary>
    public NativeDvRuntimeDescriptor? ReadDescriptorSnapshot(string? generationId)
    {
        if (!IsGenerationIdentifier(generationId)) return null;
        try
        {
            string path = DescriptorSnapshotPath(generationId!);
            if (!File.Exists(path)) return null;
            var descriptor = NativeDvRuntimeDescriptor.FromManifestJson(File.ReadAllText(path));
            return descriptor.VersionId.Equals(generationId, StringComparison.OrdinalIgnoreCase) ? descriptor : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (JsonException) { return null; }
        catch (UriFormatException) { return null; }
    }

    /// <summary>Remove recorded manifests for generations that are no longer kept.
    /// Only well-formed snapshot names are ever considered.</summary>
    public int CleanupDescriptorSnapshots(IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        string directory = Path.Combine(RootPath, DescriptorDirectoryName);
        if (!Directory.Exists(directory)) return 0;
        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!IsGenerationIdentifier(name) || keep.Contains(name)) continue;
            try { File.Delete(Path.Combine(directory, name + ".json")); removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>Declare a generation in use for as long as the handle lives, so
    /// cleanup will not touch it.
    ///
    /// This exists so a generation cannot be collected out from under a launch:
    /// between choosing a fallback and starting it, and for as long as it is
    /// playing. The pin is an exclusively held marker file outside the generation,
    /// and <see cref="IsPinned"/> is consulted by cleanup before it deletes
    /// anything.
    ///
    /// A marker is used rather than a handle on one of the runtime's own files
    /// because holding a file only makes a recursive delete fail partway: the
    /// delete removes whatever it can reach first, so the directory survives with
    /// its contents gutted, which is worse than either outcome. The generation has
    /// to be excluded before deletion is attempted, not made to fail during it.
    /// Holding the launcher is not an option either, since the Windows loader
    /// needs execute access that a delete-blocking share would refuse.
    ///
    /// Because the marker is a real exclusive file handle, it works across
    /// processes exactly as the provisioning lock does.</summary>
    public IDisposable? PinGeneration(string? generationId)
    {
        if (!IsGenerationIdentifier(generationId)) return null;
        try
        {
            string directory = Path.Combine(RootPath, PinDirectoryName);
            Directory.CreateDirectory(directory);
            return new FileStream(Path.Combine(directory, generationId! + ".pin"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether any process currently holds this generation in use.
    ///
    /// Probing is non-destructive: it opens the marker exclusively and releases it
    /// again, deleting it on close, so a stale marker from a crashed process is
    /// cleared by the first probe rather than blocking cleanup forever.</summary>
    public bool IsPinned(string? generationId)
    {
        if (!IsGenerationIdentifier(generationId)) return false;
        string path = Path.Combine(RootPath, PinDirectoryName, generationId! + ".pin");
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>A generation id is the archive SHA-256 and nothing else. This is
    /// the store's canonical spelling of that rule; the lifecycle defers to it so
    /// the two layers cannot drift apart.</summary>
    public static bool IsGenerationIdentifier(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>Re-emit a descriptor in the pinned manifest schema.
    ///
    /// Returns null when the descriptor cannot be expressed in that schema, which
    /// the parser limits to a launcher plus at most one companion. A descriptor
    /// that would not round-trip is better left unrecorded than recorded
    /// incompletely, because an incomplete manifest would validate fewer files
    /// than were actually pinned.
    ///
    /// The document is built through the serializer rather than assembled as text,
    /// so a component path or a provider name can never break out of its own
    /// string and change the shape of the manifest.</summary>
    private static string? ToManifestJson(NativeDvRuntimeDescriptor descriptor)
    {
        var launcher = descriptor.Components.FirstOrDefault(x => x.RelativePath == descriptor.LauncherRelativePath);
        if (launcher is null || descriptor.Components.Length is < 1 or > 2) return null;
        var companion = descriptor.Components.FirstOrDefault(x => x.RelativePath != descriptor.LauncherRelativePath);

        // The parser keeps whatever follows "/extracted/", so that marker has to be
        // present for a nested component path to survive the round trip.
        static Dictionary<string, object?> Entry(NativeDvRuntimeComponent component) => new()
        {
            ["pathRelativeToManifest"] = "./extracted/" + component.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
            ["bytes"] = component.Bytes,
            ["sha256"] = component.Sha256,
        };

        var runtime = new Dictionary<string, object?> { ["executable"] = Entry(launcher) };
        if (companion is not null) runtime["consoleLauncher"] = Entry(companion);

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["recordedBy"] = "adaptive-media-runtime-store",
            ["provider"] = new Dictionary<string, object?>
            {
                ["name"] = descriptor.Provider,
                ["releaseUrl"] = descriptor.ReleaseUrl,
            },
            ["archive"] = new Dictionary<string, object?>
            {
                ["url"] = descriptor.ArchiveUrl.AbsoluteUri,
                ["bytes"] = descriptor.ArchiveBytes,
                ["sha256"] = descriptor.ArchiveSha256,
            },
            ["runtime"] = runtime,
            ["mpv"] = new Dictionary<string, object?>
            {
                ["version"] = descriptor.MpvVersion,
                ["commit"] = descriptor.MpvCommit,
            },
            ["libplacebo"] = new Dictionary<string, object?> { ["apiVersion"] = descriptor.LibplaceboApi },
        }, SnapshotJson);
    }

    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = true };

    /// <summary>Remove staging generations abandoned by an interrupted install.
    /// Promoted generations are never touched.</summary>
    public int CleanupStaging()
    {
        string staging = Path.Combine(RootPath, StagingDirectoryName);
        if (!Directory.Exists(staging)) return 0;
        int removed = 0;
        foreach (string directory in Directory.EnumerateDirectories(staging))
            if (TryDelete(directory)) removed++;
        return removed;
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task DownloadAsync(Uri source, string destinationPath, CancellationToken cancellation)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Adaptive-Media-Player/0.4");
        using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        await using var output = File.Create(destinationPath);
        await input.CopyToAsync(output, cancellation);
    }

    /// <summary>Extract with the Windows-supplied bsdtar, which reads the pinned
    /// 7-Zip archive through libarchive. This avoids adding a third-party archiver
    /// as a prerequisite for playback.</summary>
    private static async Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellation)
    {
        string tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar)) throw new IOException("The Windows archive extractor (tar.exe) was not found.");
        var start = NativeProcess.StartInfo(tar, ["-x", "-f", archivePath, "-C", destinationDirectory]);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("The archive extractor could not be started.");
        var error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        if (process.ExitCode != 0) throw new IOException("The runtime archive could not be extracted: " + await error);
    }
}
