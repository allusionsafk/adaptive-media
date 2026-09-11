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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Adaptive-Media/0.4");
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
