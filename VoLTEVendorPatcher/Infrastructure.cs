using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace VoLTEVendorPatcher;

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public string CombinedOutput => Stdout + (string.IsNullOrWhiteSpace(Stderr) ? string.Empty : Environment.NewLine + Stderr);
}

internal static class ExternalProcess
{
    public static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken token)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        start.Environment["CYGWIN"] = "nodosfilewarning";
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new PatcherException("Không thể khởi chạy helper: " + executable);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
    }
}

#if false
internal sealed class LegacyToolBundle
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Required = ["debugfs.exe", "e2fsck.exe"];

    public string Require(string name)
    {
        if (_paths.TryGetValue(name, out var cached) && File.Exists(cached)) return cached;
        var externalRoot = Environment.GetEnvironmentVariable("VOLTE_PATCHER_TOOLS");
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(externalRoot) ? null : Path.Combine(externalRoot, name),
            Path.Combine(AppContext.BaseDirectory, "tools", name),
            Path.Combine(AppContext.BaseDirectory, name)
        };
        foreach (var candidate in candidates.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (File.Exists(candidate!)) { _paths[name] = candidate!; return candidate!; }
        }
        TryExtractEmbedded(name);
        if (_paths.TryGetValue(name, out cached) && File.Exists(cached)) return cached;
        throw new PatcherException($"Thiếu helper {name}. Bản phát hành cần được build kèm e2fsprogs native Windows; không dùng WSL tự động.");
    }

    private void TryExtractEmbedded(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Assets.Tools." + name, StringComparison.OrdinalIgnoreCase));
        if (resource == null) return;
        using var source = assembly.GetManifestResourceStream(resource);
        if (source == null) return;
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoLTEVendorPatcher", "tools");
        Directory.CreateDirectory(cacheRoot);
        var path = Path.Combine(cacheRoot, name);
        using (var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
        _paths[name] = path;
    }
}
#endif

internal static class EmbeddedPayload
{
    private static Assembly Assembly => Assembly.GetExecutingAssembly();

    public static async Task WriteAsync(string name, string path, string expectedSha256, CancellationToken token)
    {
        await using var source = Open(name);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var sha = SHA256.Create();
        var hash = await CopyHashAsync(source, destination, sha, token);
        if (!Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new PatcherException("Payload APK tích hợp bị sai hash.");
    }

    public static async Task WriteTextAsync(string name, string path, CancellationToken token)
    {
        await using var source = Open(name);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await source.CopyToAsync(destination, token);
    }

    private static Stream Open(string name)
    {
        var resource = Assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Assets.Payload." + name, StringComparison.OrdinalIgnoreCase));
        if (resource == null) throw new PatcherException("Thiếu payload tích hợp: " + name);
        return Assembly.GetManifestResourceStream(resource) ?? throw new PatcherException("Không đọc được payload: " + name);
    }

    private static async Task<byte[]> CopyHashAsync(Stream source, Stream destination, HashAlgorithm hash, CancellationToken token)
    {
        var buffer = new byte[81920]; int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0) { await destination.WriteAsync(buffer.AsMemory(0, read), token); hash.TransformBlock(buffer, 0, read, buffer, 0); }
        hash.TransformFinalBlock([], 0, 0);
        return hash.Hash!;
    }
}

internal static class Ext4Image
{
    public static async Task<bool> HasMagicAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length < 1082) return false;
        stream.Seek(1024 + 56, SeekOrigin.Begin);
        var magic = new byte[2]; await stream.ReadExactlyAsync(magic, token);
        return magic[0] == 0x53 && magic[1] == 0xEF;
    }
}
