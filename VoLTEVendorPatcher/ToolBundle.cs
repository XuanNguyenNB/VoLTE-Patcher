using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace VoLTEVendorPatcher;

/// <summary>Locates, extracts, and verifies the native ext4 helpers.</summary>
internal sealed class ToolBundle
{
    private sealed record ToolManifest(string Version, IReadOnlyDictionary<string, string> Files);
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<ToolManifest> _manifest = new(LoadManifest, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object ExtractLock = new();

    public string Require(string name)
    {
        if (_paths.TryGetValue(name, out var cached) && Verify(cached, name)) return cached;
        var externalRoot = Environment.GetEnvironmentVariable("VOLTE_PATCHER_TOOLS");
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(externalRoot) ? null : Path.Combine(externalRoot, name),
            Path.Combine(AppContext.BaseDirectory, "tools", name),
            Path.Combine(AppContext.BaseDirectory, name)
        };
        foreach (var candidate in candidates.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (File.Exists(candidate!) && Verify(candidate!, name)) { _paths[name] = candidate!; return candidate!; }
        }
        ExtractEmbeddedBundle();
        if (_paths.TryGetValue(name, out cached) && Verify(cached, name)) return cached;
        throw new PatcherException($"Thiếu helper {name}. Bản phát hành cần được build kèm e2fsprogs native Windows; không dùng WSL tự động.");
    }

    private static ToolManifest LoadManifest()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Assets.Tools.tools.lock.json", StringComparison.OrdinalIgnoreCase));
        if (resource == null) throw new PatcherException("Thiếu tools.lock.json trong bản build.");
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new PatcherException("Không đọc được tools.lock.json.");
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new PatcherException("tools.lock.json không hợp lệ.");
        var version = document.RootElement.TryGetProperty("bundleVersion", out var versionValue) ? versionValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(version)) throw new PatcherException("tools.lock.json thiếu bundleVersion.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.EnumerateObject())
        {
            var hash = item.Value.GetString();
            if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new PatcherException("Hash helper trong tools.lock.json không hợp lệ: " + item.Name);
            result[item.Name] = hash.ToUpperInvariant();
        }
        return new ToolManifest(version, result);
    }

    private bool Verify(string path, string name)
    {
        if (!File.Exists(path) || !_manifest.Value.Files.TryGetValue(name, out var expected)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var actual = Convert.ToHexString(sha.ComputeHash(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new PatcherException($"Helper {name} không khớp SHA-256 trong tools.lock.json.");
        return true;
    }

    private void ExtractEmbeddedBundle()
    {
        lock (ExtractLock)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var safeVersion = string.Concat(_manifest.Value.Version.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoLTEVendorPatcher", "tools", safeVersion);
            Directory.CreateDirectory(cacheRoot);
            foreach (var item in _manifest.Value.Files)
            {
                var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Assets.Tools." + item.Key, StringComparison.OrdinalIgnoreCase));
                if (resource == null) continue;
                var path = Path.Combine(cacheRoot, item.Key);
                if (File.Exists(path) && Verify(path, item.Key)) { _paths[item.Key] = path; continue; }
                using var source = assembly.GetManifestResourceStream(resource) ?? throw new PatcherException("Không đọc được helper tích hợp: " + item.Key);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
                if (!Verify(temporary, item.Key)) { TryDelete(temporary); throw new PatcherException("Helper tích hợp sai hash: " + item.Key); }
                File.Move(temporary, path, true);
                _paths[item.Key] = path;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
