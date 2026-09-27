using System.Security.Cryptography;
using System.Text.Json;

namespace AdaptiveMedia;

/// <summary>Resolves the installed runtime under the app data directory:
/// runtimes/generated-motion/current.json names a folder whose manifest.json lists
/// every file with its SHA-256. Validation is cached per file stamp.</summary>
public static class GeneratedMotionRuntime
{
    public static string DefaultRootPath => Path.Combine(SettingsStore.DirectoryPath, "runtimes", "generated-motion");
    private static readonly string[] Critical = ["mpv.exe", "avfilter-12.dll", "NvOFFRUC.dll"];
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
            var stamp = new FileInfo(manifestPath);
            string key = dir + "|" + stamp.Length + "|" + stamp.LastWriteTimeUtc.Ticks + "|" +
                string.Join("|", Critical.Select(f => new FileInfo(Path.Combine(dir, f))).Select(f => f.Exists ? f.Length + ":" + f.LastWriteTimeUtc.Ticks : "missing"));
            lock (Gate) if (_cache is { } hit && hit.Key == key) return hit.Backend;
            var backend = Validate(dir, id, manifestPath);
            lock (Gate) _cache = (key, backend);
            return backend;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static GeneratedMotionBackend? Validate(string dir, string id, string manifestPath)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var rootElement = manifest.RootElement;
        if (!rootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
            !rootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return null;
        var listed = files.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
        if (Critical.Any(f => !listed.ContainsKey(f))) return null;
        foreach (var (name, expected) in listed)
        {
            if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)) return null;
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) return null;
            // Every file is present; the ones that define behaviour are also hashed.
            if (Critical.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase)) return null;
            }
        }
        string description = rootElement.TryGetProperty("description", out var d) ? d.GetString() ?? id : id;
        return new(Path.Combine(dir, "mpv.exe"), id, description);
    }
}
