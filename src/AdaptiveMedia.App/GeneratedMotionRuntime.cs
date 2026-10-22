using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdaptiveMedia;

/// <summary>Resolves the installed runtime under the app data directory:
/// runtimes/generated-motion/current.json names a folder whose manifest.json lists
/// every file with its SHA-256. Validation is cached per file stamp.</summary>
public static class GeneratedMotionRuntime
{
    public static string DefaultRootPath => Path.Combine(SettingsStore.DirectoryPath, "runtimes", "generated-motion");
    private static readonly string[] Required = ["mpv.exe", "avfilter-12.dll", "NvOFFRUC.dll"];
    private static readonly object Gate = new();
    private static (string Key, GeneratedMotionBackend? Backend)? _cache;

    public static GeneratedMotionBackend? Resolve(string? rootPath = null)
    {
        string root = rootPath ?? DefaultRootPath;
        try
        {
            string pointer = Path.Combine(root, "current.json");
            if (!File.Exists(pointer)) return null;
            using var current = JsonDocument.Parse(File.ReadAllText(pointer));
            string? id = current.RootElement.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..") return null;
            string dir = Path.Combine(root, id);
            string manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) return null;
            string manifestJson = File.ReadAllText(manifestPath);
            using var manifest = JsonDocument.Parse(manifestJson);
            var rootElement = manifest.RootElement;
            if (!rootElement.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out int version) || version != 1 ||
                !rootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return null;
            var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.EnumerateObject())
            {
                string name = file.Name;
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal) ||
                    name.EndsWith('.') || name.EndsWith(' ') || file.Value.ValueKind != JsonValueKind.String ||
                    !listed.TryAdd(name, file.Value.GetString()!)) return null;
            }
            if (Required.Any(f => !listed.ContainsKey(f))) return null;
            string description = id;
            if (rootElement.TryGetProperty("description", out var d))
            {
                if (d.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return null;
                description = d.GetString() ?? id;
            }
            // Bind cached validation to the exact manifest content. Component stamps
            // avoid rehashing the runtime on every preparation; deliberate rewrites
            // preserving both length and timestamp still require a cold validation.
            string key = dir + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson))) + "|" +
                string.Join("|", listed.Keys.Select(f => new FileInfo(Path.Combine(dir, f))).Select(f => f.Exists ? f.Length + ":" + f.LastWriteTimeUtc.Ticks : "missing"));
            lock (Gate) if (_cache is { } hit && hit.Key == key) return hit.Backend;
            var backend = Validate(dir, id, description, listed);
            lock (Gate) _cache = (key, backend);
            return backend;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static GeneratedMotionBackend? Validate(string dir, string id, string description, IReadOnlyDictionary<string, string> listed)
    {
        foreach (var (name, expected) in listed)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) return null;
            // Any library in the installed runtime can affect playback, so the
            // installer's manifest covers every component rather than a shortlist.
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return new(Path.Combine(dir, "mpv.exe"), id, description);
    }
}
