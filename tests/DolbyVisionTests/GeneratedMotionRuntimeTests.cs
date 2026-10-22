using AdaptiveMedia;
using System.Security.Cryptography;
using System.Text.Json;

internal static class GeneratedMotionRuntimeTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "adaptive-generated-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            string generation = Path.Combine(root, "test-runtime");
            Directory.CreateDirectory(generation);
            File.WriteAllText(Path.Combine(root, "current.json"), "{\"id\":\"test-runtime\"}");
            string manifest = Path.Combine(generation, "manifest.json");
            string[] names = ["mpv.exe", "avfilter-12.dll", "NvOFFRUC.dll", "avcodec-62.dll"];
            var hashes = new Dictionary<string, string>();
            foreach (string name in names)
            {
                string path = Path.Combine(generation, name);
                File.WriteAllText(path, "fixture component " + name);
                hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
            string valid = JsonSerializer.Serialize(new { schemaVersion = 1, description = "test fixture", files = hashes });
            void WriteManifest(string json) => File.WriteAllText(manifest, json);
            void Check(bool result, string message) => check(result, "GENERATED RUNTIME: " + message);

            WriteManifest(valid);
            Check(GeneratedMotionRuntime.Resolve(root)?.Executable == Path.Combine(generation, "mpv.exe"),
                "a complete manifest with matching component hashes resolves");

            string dependency = Path.Combine(generation, "avcodec-62.dll");
            byte[] original = File.ReadAllBytes(dependency);
            File.WriteAllText(dependency, "damaged dependency with different length");
            Check(GeneratedMotionRuntime.Resolve(root) is null,
                "a changed dependency invalidates a previously validated runtime");
            // A fresh resolver/cache key must also hash components beyond the three required names.
            WriteManifest(valid + " ");
            Check(GeneratedMotionRuntime.Resolve(root) is null,
                "a dependency hash mismatch refuses a cold validation");
            File.WriteAllBytes(dependency, original);
            Check(GeneratedMotionRuntime.Resolve(root) is not null, "repairing the dependency restores validation");
            File.Delete(dependency);
            Check(GeneratedMotionRuntime.Resolve(root) is null,
                "a missing dependency invalidates a previously validated runtime");
            File.WriteAllBytes(dependency, original);
            Check(GeneratedMotionRuntime.Resolve(root) is not null, "restoring a deleted dependency restores validation");
            var manifestStamp = File.GetLastWriteTimeUtc(manifest);
            WriteManifest((valid + " ").Replace(hashes["avcodec-62.dll"], new string('0', 64)));
            File.SetLastWriteTimeUtc(manifest, manifestStamp);
            Check(GeneratedMotionRuntime.Resolve(root) is null,
                "a manifest digest change invalidates the cache even with the original length and timestamp");

            string[] malformed =
            [
                valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2147483648"),
                valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.5"),
                valid.Replace("\"mpv.exe\":", "\"MPV.EXE\":\"duplicate\",\"mpv.exe\":"),
                valid.Replace("\"mpv.exe\":", "\"mpv.exe\":\"duplicate\",\"mpv.exe\":"),
                valid.Replace("\"avcodec-62.dll\":", "\"../avcodec-62.dll\":"),
                valid.Replace("\"avcodec-62.dll\":", "\"avcodec-62.dll:stream\":"),
                valid.Replace("\"files\":{", "\"files\":{\"\":\"invalid\","),
                valid.Replace("\"description\":\"test fixture\"", "\"description\":42"),
                "[]",
                "null",
            ];
            foreach (string json in malformed)
            {
                WriteManifest(json);
                Check(GeneratedMotionRuntime.Resolve(root) is null, "malformed manifest refuses the optional runtime without throwing");
            }
            WriteManifest(valid);
            File.WriteAllText(Path.Combine(root, "current.json"), "{\"id\":42}");
            Check(GeneratedMotionRuntime.Resolve(root) is null, "a malformed generation pointer refuses the optional runtime");
            File.WriteAllText(Path.Combine(root, "current.json"), "{\"id\":\"..\"}");
            Check(GeneratedMotionRuntime.Resolve(root) is null, "a parent-directory generation pointer is refused");
            File.WriteAllText(Path.Combine(root, "current.json"), "[]");
            Check(GeneratedMotionRuntime.Resolve(root) is null, "a nonobject generation pointer refuses the optional runtime");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
