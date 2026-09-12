using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives.Rar;

namespace VoLTEVendorPatcher;

internal enum ImageFormat { RawExt4, Sparse }
internal enum PatchProfile { LegacyApi27, ModernApi28Plus }
internal enum PatchState { Unknown, Patchable, AlreadyPatched, Conflict }

internal sealed class AnalysisResult
{
    public required string InputPath { get; init; }
    public required ImageFormat Format { get; init; }
    public required long FileSize { get; init; }
    public required long RawFileSize { get; init; }
    public required string Board { get; init; }
    public required string Platform { get; init; }
    public required int FirstApiLevel { get; init; }
    public required PatchProfile Profile { get; init; }
    public required PatchState State { get; init; }
    public required bool HasImsStack { get; init; }
    public required bool HasSelinuxXattr { get; init; }
    public required long FreeBlocks { get; init; }
    public required long FreeInodes { get; init; }
    public required string BuildPropHash { get; init; }
    public required string Message { get; init; }
    public string? ArchiveEntryName { get; init; }
    public bool CanPatch => State is PatchState.Patchable && HasImsStack && HasSelinuxXattr;

    public string ToDisplayText()
    {
        var state = State switch
        {
            PatchState.Patchable => "PATCHABLE",
            PatchState.AlreadyPatched => "ALREADY PATCHED",
            PatchState.Conflict => "CONFLICT",
            _ => "NOT COMPATIBLE"
        };
        var format = ArchiveEntryName == null ? Format.ToString() : $"RAR → {Format}";
        return $"State       : {state}\r\n" +
               $"Format      : {format}\r\n" +
               $"Size        : {FileSize:N0} bytes\r\n" +
               $"Board       : {Board}\r\n" +
               $"Platform    : {Platform}\r\n" +
               $"First API   : {FirstApiLevel}\r\n" +
               $"Profile     : {Profile}\r\n" +
               $"IMS stack   : {(HasImsStack ? "yes" : "no")}\r\n" +
               $"SELinux EA  : {(HasSelinuxXattr ? "yes" : "no")}\r\n" +
               $"Free blocks : {FreeBlocks:N0}\r\n" +
               $"Free inodes : {FreeInodes:N0}\r\n" +
               $"build.prop  : {BuildPropHash}";
    }
}

internal sealed class PatchResult
{
    public required string OutputPath { get; init; }
    public required string OutputHash { get; init; }
    public required AnalysisResult Analysis { get; init; }
    public required string Message { get; init; }

    public string ToDisplayText() => Analysis.ToDisplayText() + $"\r\nOutput hash : {OutputHash}\r\nOutput      : {OutputPath}";
}

internal sealed class PatcherException : Exception
{
    public PatcherException(string message) : base(message) { }
}

internal sealed class PatcherEngine
{
    private const string OverlaySha256 = "1010C1C7855C0150C204C7E6376F665FCB3B55C609BDFFAFA8114B0952B81694";
    private const string CanonicalOverlay = "/overlay/GAQVoLTE/GAQ_OPPO_MTK_VoLTE_Overlay.apk";
    private const string CanonicalInit = "/etc/init/gaq_oppo_mtk_volte.rc";
    private static readonly string[] RequiredBinaries = ["volte_ua", "volte_stack", "volte_imcb", "volte_imsm_93", "wfca"];
    private static readonly string[] RequiredInitFragments = ["init.volte_ua.rc", "init.volte_stack.rc", "init.volte_imsm_93.rc", "init.volte_imcb.rc", "init.wfca.rc"];
    private readonly ToolBundle _tools = new();

    public async Task<AnalysisResult> AnalyzeAsync(string inputPath, IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath)) throw new PatcherException("Không tìm thấy image.");
        progress?.Report(new(5, "Nhận diện định dạng image…"));
        var detected = await SparseImage.DetectAsync(fullPath, token);
        var sourceImage = fullPath;
        string? archiveRoot = null;
        string? archiveEntryName = null;
        string? rawStaging = null;
        try
        {
            if (IsRarPath(fullPath))
            {
                archiveRoot = Path.Combine(Path.GetTempPath(), "volte-rar-analyze-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(archiveRoot);
                sourceImage = Path.Combine(archiveRoot, "vendor.img");
                progress?.Report(new(7, "Giải nén vendor.img từ RAR…"));
                archiveEntryName = await ExtractVendorFromRarAsync(fullPath, sourceImage, progress, token);
                detected = await SparseImage.DetectAsync(sourceImage, token);
            }

            var rawPath = sourceImage;
            if (detected.Format == ImageFormat.Sparse)
            {
                rawStaging = Path.Combine(Path.GetTempPath(), "volte-analyze-" + Guid.NewGuid().ToString("N") + ".raw");
                progress?.Report(new(12, "Giải sparse image để phân tích…"));
                await SparseImage.ToRawAsync(sourceImage, rawStaging, detected.SparseHeader!.Value, progress, token);
                rawPath = rawStaging;
            }
            return await AnalyzeRawAsync(fullPath, rawPath, detected.Format,
                new FileInfo(sourceImage).Length,
                detected.SparseHeader?.RawLength ?? new FileInfo(sourceImage).Length,
                archiveEntryName, progress, token);
        }
        finally
        {
            TryDelete(rawStaging);
            TryDeleteDirectory(archiveRoot);
        }
    }

    public async Task<PatchResult> PatchAsync(AnalysisResult analysis, string outputPath, IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        if (!analysis.CanPatch) throw new PatcherException("Image không ở trạng thái có thể patch.");
        var input = Path.GetFullPath(analysis.InputPath);
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output)) throw new PatcherException("Output đã tồn tại; hãy chọn tên khác.");
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) throw new PatcherException("Output phải khác image gốc.");
        var outputDirectory = Path.GetDirectoryName(output) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(outputDirectory);
        SparseHeader? sparseHeader = null;
        if (analysis.Format == ImageFormat.Sparse)
        {
            if (analysis.ArchiveEntryName == null)
                sparseHeader = (await SparseImage.DetectAsync(input, token)).SparseHeader!.Value;
            EnsureFreeSpace(outputDirectory,
                checked(analysis.RawFileSize + analysis.FileSize + 32L * 1024 * 1024));
        }
        else
        {
            EnsureFreeSpace(outputDirectory, checked(analysis.FileSize + 32L * 1024 * 1024));
        }
        var runRoot = Path.Combine(Path.GetTempPath(), "VoLTEVendorPatcher", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var partial = output + ".partial";
        var rawWork = analysis.Format == ImageFormat.Sparse ? Path.Combine(runRoot, "vendor.raw") : partial;
        try
        {
            if (analysis.ArchiveEntryName != null)
            {
                if (analysis.Format == ImageFormat.Sparse)
                {
                    var archivedSparse = Path.Combine(runRoot, "vendor.simg");
                    progress?.Report(new(5, "Giải nén vendor.img từ RAR…"));
                    await ExtractVendorFromRarAsync(input, archivedSparse, progress, token);
                    sparseHeader = (await SparseImage.DetectAsync(archivedSparse, token)).SparseHeader!.Value;
                    progress?.Report(new(20, "Chuyển sparse sang raw…"));
                    await SparseImage.ToRawAsync(archivedSparse, rawWork, sparseHeader.Value, progress, token);
                }
                else
                {
                    progress?.Report(new(5, "Giải nén vendor.img từ RAR…"));
                    await ExtractVendorFromRarAsync(input, rawWork, progress, token);
                }
            }
            else if (analysis.Format == ImageFormat.Sparse)
            {
                progress?.Report(new(5, "Chuyển sparse sang raw…"));
                await SparseImage.ToRawAsync(input, rawWork, sparseHeader!.Value, progress, token);
            }
            else
            {
                progress?.Report(new(5, "Sao chép image vào staging…"));
                await CopyWithProgressAsync(input, rawWork, progress, token);
            }

            progress?.Report(new(45, "Ghi payload IMS/VoLTE…"));
            await PatchRawAsync(rawWork, analysis.Profile, analysis, runRoot, progress, token);
            progress?.Report(new(65, "Xác minh ext4 và payload…"));
            await VerifyPatchedAsync(rawWork, analysis, runRoot, token);

            if (analysis.Format == ImageFormat.Sparse)
            {
                progress?.Report(new(78, "Đóng gói lại sparse image…"));
                await SparseImage.ToSparseAsync(rawWork, partial, 4096, progress, token);
                progress?.Report(new(90, "Xác minh sparse output…"));
                var verifyRaw = Path.Combine(runRoot, "verify.raw");
                var outHeader = (await SparseImage.DetectAsync(partial, token)).SparseHeader!.Value;
                await SparseImage.ToRawAsync(partial, verifyRaw, outHeader, null, token);
                await VerifyPatchedAsync(verifyRaw, analysis, runRoot, token);
            }
            var hash = await HashFileAsync(partial, token);
            File.Move(partial, output);
            progress?.Report(new(100, "Hoàn tất", $"SHA-256 output: {hash}"));
            return new PatchResult { OutputPath = output, OutputHash = hash, Analysis = analysis, Message = "Patch thành công; image gốc không bị sửa." };
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        finally { TryDeleteDirectory(runRoot); }
    }

    private async Task<AnalysisResult> AnalyzeRawAsync(string original, string rawPath, ImageFormat format,
        long imageSize, long rawFileSize, string? archiveEntryName,
        IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        if (!await Ext4Image.HasMagicAsync(rawPath, token)) throw new PatcherException("Image sau khi giải nén không phải ext4.");
        var debugfs = _tools.Require("debugfs.exe");
        var e2fsck = _tools.Require("e2fsck.exe");
        progress?.Report(new(20, "Kiểm tra filesystem ext4…"));
        var fsck = await ExternalProcess.RunAsync(e2fsck, ["-fn", rawPath], null, token);
        if (fsck.ExitCode != 0) throw new PatcherException("Filesystem không sạch hoặc e2fsck không thể kiểm tra:\n" + fsck.CombinedOutput.Trim());
        progress?.Report(new(30, "Đọc build.prop và IMS stack…"));
        var buildProp = await DebugfsCatAsync(debugfs, rawPath, "/build.prop", token);
        var props = ParseProperties(buildProp);
        var manufacturer = FirstValue(props,
            "ro.product.manufacturer",
            "ro.vendor.product.manufacturer",
            "ro.product.vendor.manufacturer",
            "ro.odm.product.manufacturer");
        var brand = FirstValue(props,
            "ro.product.brand",
            "ro.vendor.product.brand",
            "ro.product.vendor.brand",
            "ro.odm.product.brand");
        var device = FirstValue(props,
            "ro.product.device",
            "ro.vendor.product.device",
            "ro.product.vendor.device",
            "ro.odm.product.device");
        var board = FirstValue(props, "ro.product.board")
                    ?? device
                    ?? brand
                    ?? manufacturer
                    ?? "unknown";
        var platform = FirstValue(props,
            "ro.board.platform",
            "ro.mediatek.platform",
            "ro.vendor.mediatek.platform") ?? "unknown";
        var api = ParseInt(FirstValue(props,
            "ro.product.first_api_level",
            "ro.board.first_api_level",
            "ro.vendor.build.version.sdk")) ?? -1;
        var allIdentity = string.Join(' ', new[]
        {
            board,
            manufacturer,
            brand,
            device
        }.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        if (!platform.StartsWith("mt", StringComparison.OrdinalIgnoreCase)) throw new PatcherException($"Không phải nền tảng MediaTek: {platform}");
        if (!(allIdentity.Contains("oppo") || allIdentity.Contains("realme") || board.StartsWith("rm", StringComparison.OrdinalIgnoreCase))) throw new PatcherException($"Không nhận diện được OPPO/Realme board: {board}");
        var profileDefinition = PatchProfileCatalog.Resolve(api);
        if (profileDefinition == null)
            throw new PatcherException($"API đầu tiên {api} ngoài các profile đã kiểm chứng ({PatchProfileCatalog.SupportedApiSummary}; Android 8.1–10).");
        var profile = profileDefinition.Id;
        var binListing = await DebugfsRunAsync(debugfs, rawPath, "ls -l /bin", token);
        var initListing = await DebugfsRunAsync(debugfs, rawPath, "ls -l /etc/init", token);
        var missingBinaries = RequiredBinaries.Where(binary =>
            !Regex.IsMatch(binListing, $@"\b{Regex.Escape(binary)}\b")).ToArray();
        var missingInit = RequiredInitFragments.Where(fragment =>
            !Regex.IsMatch(initListing, $@"\b{Regex.Escape(fragment)}\b")).ToArray();
        var hasStack = missingBinaries.Length == 0 && missingInit.Length == 0;
        if (!hasStack)
        {
            var details = new List<string>();
            if (missingBinaries.Length > 0) details.Add("binary: " + string.Join(", ", missingBinaries));
            if (missingInit.Length > 0) details.Add("init: " + string.Join(", ", missingInit));
            throw new PatcherException("IMS/VoLTE stack chưa đầy đủ; thiếu " + string.Join("; ", details) + ".");
        }
        var stats = await DebugfsRunAsync(debugfs, rawPath, "stats", token);
        var freeBlocks = ParseLong(stats, @"Free blocks:\s*(\d+)");
        var freeInodes = ParseLong(stats, @"Free inodes:\s*(\d+)");
        if (freeBlocks < 8 || freeInodes < 3)
            throw new PatcherException($"Không đủ chỗ trống ext4 (cần tối thiểu 8 block và 3 inode; còn {freeBlocks} block/{freeInodes} inode).");
        var hasVendorLabel = Regex.IsMatch(stats, @"volume name\s*[:=]\s*vendor(?:\s|$)", RegexOptions.IgnoreCase);
        var wasMountedAsVendor = Regex.IsMatch(stats, @"last mounted on\s*[:=]\s*/vendor/?(?:\s|$)", RegexOptions.IgnoreCase);
        if (!hasVendorLabel && !wasMountedAsVendor)
            throw new PatcherException("Filesystem không được nhận diện là vendor (thiếu volume label và lịch sử mount /vendor). ");
        var ea = await DebugfsRunAsync(debugfs, rawPath, "ea_list /overlay", token);
        var hasEa = ea.Contains("security.selinux", StringComparison.OrdinalIgnoreCase);
        var buildHash = await HashDebugfsFileAsync(debugfs, rawPath, "/build.prop", token);
        var state = await DetectPatchStateAsync(debugfs, rawPath, profile, token);
        var message = state switch
        {
            PatchState.AlreadyPatched => "Đã tìm thấy payload VoLTE hợp lệ trong image.",
            PatchState.Conflict => "Phát hiện payload trùng đường dẫn nhưng nội dung khác.",
            _ when !hasEa => "Thiếu SELinux extended attribute trên /overlay.",
            _ => "Image tương thích với profile " + profile
        };
        if (!hasEa) state = PatchState.Conflict;
        return new AnalysisResult { InputPath = original, Format = format, FileSize = imageSize, RawFileSize = rawFileSize, Board = board, Platform = platform, FirstApiLevel = api, Profile = profile, State = state, HasImsStack = hasStack, HasSelinuxXattr = hasEa, FreeBlocks = freeBlocks, FreeInodes = freeInodes, BuildPropHash = buildHash, Message = message, ArchiveEntryName = archiveEntryName };
    }

    private async Task PatchRawAsync(string rawPath, PatchProfile profile, AnalysisResult analysis, string runRoot, IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        var profileDefinition = PatchProfileCatalog.Get(profile);
        var debugfs = _tools.Require("debugfs.exe");
        var payloadDir = Path.Combine(runRoot, "payload");
        Directory.CreateDirectory(payloadDir);
        await EmbeddedPayload.WriteAsync("GAQ_OPPO_MTK_VoLTE_Overlay.apk", Path.Combine(payloadDir, "payload.apk"), OverlaySha256, token);
        var initPayload = Path.Combine(payloadDir, "init.rc");
        await EmbeddedPayload.WriteTextAsync(profileDefinition.InitTemplate, initPayload, token);
        var expectedInitHash = profileDefinition.InitSha256;
        if (!(await HashFileAsync(initPayload, token)).Equals(expectedInitHash, StringComparison.OrdinalIgnoreCase))
            throw new PatcherException("Init template tích hợp bị sai hash.");
        await File.WriteAllBytesAsync(Path.Combine(payloadDir, "overlay.label"), Encoding.UTF8.GetBytes("u:object_r:vendor_overlay_file:s0\0"), token);
        await File.WriteAllBytesAsync(Path.Combine(payloadDir, "config.label"), Encoding.UTF8.GetBytes("u:object_r:vendor_configs_file:s0\0"), token);
        var state = await DetectPatchStateAsync(debugfs, rawPath, profile, token);
        if (state == PatchState.Conflict) throw new PatcherException("Payload hiện hữu bị xung đột; tool không ghi đè.");
        var commandLines = new List<string>();
        var canonicalDirExists = await DebugfsPathExistsAsync(debugfs, rawPath, "/overlay/GAQVoLTE", token);
        if (!canonicalDirExists)
        {
            // The Windows debugfs port resolves write destinations relative to its
            // current ext4 directory; using an absolute destination creates a
            // literal name containing slashes.  Change directory before writes.
            commandLines.Add("cd /overlay");
            commandLines.Add("mkdir GAQVoLTE");
        }
        commandLines.Add("set_inode_field /overlay/GAQVoLTE uid 0");
        commandLines.Add("set_inode_field /overlay/GAQVoLTE gid 2000");
        commandLines.Add("set_inode_field /overlay/GAQVoLTE mode 040755");
        commandLines.Add("cd /overlay/GAQVoLTE");
        if (!await KnownFileMatchesAsync(debugfs, rawPath, CanonicalOverlay, OverlaySha256, payloadDir, token))
        {
            commandLines.Add("write payload.apk GAQ_OPPO_MTK_VoLTE_Overlay.apk");
            commandLines.Add("set_inode_field GAQ_OPPO_MTK_VoLTE_Overlay.apk uid 0");
            commandLines.Add("set_inode_field GAQ_OPPO_MTK_VoLTE_Overlay.apk gid 0");
            commandLines.Add("set_inode_field GAQ_OPPO_MTK_VoLTE_Overlay.apk mode 0100644");
            commandLines.Add("ea_set -f overlay.label GAQ_OPPO_MTK_VoLTE_Overlay.apk security.selinux");
        }
        if (!await InitMatchesAsync(debugfs, rawPath, profile, CanonicalInit, token))
        {
            commandLines.Add("cd /etc/init");
            commandLines.Add("write init.rc gaq_oppo_mtk_volte.rc");
            commandLines.Add("set_inode_field gaq_oppo_mtk_volte.rc uid 0");
            commandLines.Add("set_inode_field gaq_oppo_mtk_volte.rc gid 0");
            commandLines.Add("set_inode_field gaq_oppo_mtk_volte.rc mode 0100644");
            commandLines.Add("ea_set -f config.label gaq_oppo_mtk_volte.rc security.selinux");
        }
        commandLines.Add("ea_set -f overlay.label /overlay/GAQVoLTE security.selinux");
        var commandFile = Path.Combine(payloadDir, "commands.txt");
        await File.WriteAllLinesAsync(commandFile, commandLines, new UTF8Encoding(false), token);
        var result = await ExternalProcess.RunAsync(debugfs, ["-w", "-f", commandFile, rawPath], payloadDir, token);
        if (result.ExitCode != 0) throw new PatcherException("debugfs patch thất bại:\n" + result.CombinedOutput.Trim());
        progress?.Report(new(60, "Đã ghi overlay và init profile " + profile));
    }

    private async Task VerifyPatchedAsync(string rawPath, AnalysisResult analysis, string runRoot, CancellationToken token)
    {
        if (!await Ext4Image.HasMagicAsync(rawPath, token)) throw new PatcherException("Output mất ext4 magic sau patch.");
        var debugfs = _tools.Require("debugfs.exe");
        var e2fsck = _tools.Require("e2fsck.exe");
        var fsck = await ExternalProcess.RunAsync(e2fsck, ["-fn", rawPath], null, token);
        if (fsck.ExitCode != 0) throw new PatcherException("Filesystem output không sạch:\n" + fsck.CombinedOutput.Trim());
        var checkDir = Path.Combine(runRoot, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(checkDir);
        try
        {
            var apk = Path.Combine(checkDir, "overlay.apk");
            var init = Path.Combine(checkDir, "init.rc");
            await DebugfsDumpAsync(debugfs, rawPath, CanonicalOverlay, apk, checkDir, token);
            await DebugfsDumpAsync(debugfs, rawPath, CanonicalInit, init, checkDir, token);
            var apkHash = await HashFileAsync(apk, token);
            if (!apkHash.Equals(OverlaySha256, StringComparison.OrdinalIgnoreCase)) throw new PatcherException("APK overlay hash không khớp.");
            var initText = await File.ReadAllTextAsync(init, token);
            var initHash = await HashFileAsync(init, token);
            var expectedInitHash = PatchProfileCatalog.Get(analysis.Profile).InitSha256;
            if (!initHash.Equals(expectedInitHash, StringComparison.OrdinalIgnoreCase)) throw new PatcherException("Init profile hash không khớp.");
            foreach (var required in ProfileLines(analysis.Profile)) if (!initText.Contains(required, StringComparison.Ordinal)) throw new PatcherException("Init profile thiếu dòng: " + required);
            var stat = await DebugfsRunAsync(debugfs, rawPath, "stat /overlay/GAQVoLTE", token);
            var apkMetadata = await DebugfsRunAsync(debugfs, rawPath, "stat " + CanonicalOverlay, token);
            var initMetadata = await DebugfsRunAsync(debugfs, rawPath, "stat " + CanonicalInit, token);
            if (!apkMetadata.Contains("Mode:  0644", StringComparison.Ordinal) || !apkMetadata.Contains("vendor_overlay_file", StringComparison.Ordinal) ||
                !initMetadata.Contains("Mode:  0644", StringComparison.Ordinal) || !initMetadata.Contains("vendor_configs_file", StringComparison.Ordinal)) throw new PatcherException("Metadata overlay/init không đúng.");
            if (!stat.Contains("Mode:  0755", StringComparison.Ordinal) || !stat.Contains("vendor_overlay_file", StringComparison.Ordinal)) throw new PatcherException("Metadata overlay không đúng.");
            var buildHash = await HashDebugfsFileAsync(debugfs, rawPath, "/build.prop", token);
            if (!buildHash.Equals(analysis.BuildPropHash, StringComparison.OrdinalIgnoreCase)) throw new PatcherException("build.prop bị thay đổi ngoài phạm vi.");
        }
        finally { TryDeleteDirectory(checkDir); }
    }

    private async Task<PatchState> DetectPatchStateAsync(string debugfs, string rawPath, PatchProfile profile, CancellationToken token)
    {
        var overlayPaths = new List<string> { CanonicalOverlay, "/overlay/CPH1859VoLTE/CPH1859_VN_VoLTE_Overlay.apk", "/overlay/GAQVoLTE/GAQ_OPPO_MTK_VoLTE_Overlay.apk" };
        var hasOverlay = false;
        foreach (var path in overlayPaths.Distinct())
        {
            if (await DebugfsPathExistsAsync(debugfs, rawPath, path, token))
            {
                if (!await KnownFileMatchesAsync(debugfs, rawPath, path, OverlaySha256, Path.GetTempPath(), token)) return PatchState.Conflict;
                hasOverlay = true;
            }
        }
        var initPaths = new[] { CanonicalInit, "/etc/init/cph1859_volte_force.rc" };
        var hasInit = false;
        foreach (var path in initPaths)
        {
            if (await DebugfsPathExistsAsync(debugfs, rawPath, path, token))
            {
                var text = await DebugfsCatAsync(debugfs, rawPath, path, token);
                if (ProfileLines(profile).All(text.Contains)) hasInit = true; else return PatchState.Conflict;
            }
        }
        return hasOverlay && hasInit ? PatchState.AlreadyPatched : PatchState.Patchable;
    }

    private async Task<bool> InitMatchesAsync(string debugfs, string rawPath, PatchProfile profile, string path, CancellationToken token)
    {
        if (!await DebugfsPathExistsAsync(debugfs, rawPath, path, token)) return false;
        var text = await DebugfsCatAsync(debugfs, rawPath, path, token);
        return ProfileLines(profile).All(text.Contains);
    }

    private async Task<bool> KnownFileMatchesAsync(string debugfs, string rawPath, string path, string expectedHash, string workingDirectory, CancellationToken token)
    {
        if (!await DebugfsPathExistsAsync(debugfs, rawPath, path, token)) return false;
        var dir = Path.Combine(workingDirectory, "volte-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var output = Path.Combine(dir, "probe.bin");
            await DebugfsDumpAsync(debugfs, rawPath, path, output, dir, token);
            return (await HashFileAsync(output, token)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDeleteDirectory(dir); }
    }

    private async Task<bool> DebugfsPathExistsAsync(string debugfs, string rawPath, string path, CancellationToken token)
    {
        var result = await ExternalProcess.RunAsync(debugfs, ["-R", "stat " + path, rawPath], null, token);
        return result.ExitCode == 0 && !result.CombinedOutput.Contains("File not found", StringComparison.OrdinalIgnoreCase) && !result.CombinedOutput.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private async Task DebugfsDumpAsync(string debugfs, string rawPath, string path, string output, string workingDirectory, CancellationToken token)
    {
        var commandFile = Path.Combine(workingDirectory, "dump-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(commandFile, "dump " + path + " probe.bin\n", new UTF8Encoding(false), token);
        var result = await ExternalProcess.RunAsync(debugfs, ["-f", commandFile, rawPath], workingDirectory, token);
        TryDelete(commandFile);
        if (result.ExitCode != 0 || !File.Exists(Path.Combine(workingDirectory, "probe.bin"))) throw new PatcherException("Không thể đọc file trong vendor: " + path);
        File.Move(Path.Combine(workingDirectory, "probe.bin"), output, true);
    }

    private async Task<string> HashDebugfsFileAsync(string debugfs, string rawPath, string path, CancellationToken token)
    {
        var dir = Path.Combine(Path.GetTempPath(), "volte-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "data");
            await DebugfsDumpAsync(debugfs, rawPath, path, file, dir, token);
            return await HashFileAsync(file, token);
        }
        finally { TryDeleteDirectory(dir); }
    }

    private async Task<string> DebugfsCatAsync(string debugfs, string rawPath, string path, CancellationToken token) => (await DebugfsRunAsync(debugfs, rawPath, "cat " + path, token)).TrimEnd('\0');
    private async Task<string> DebugfsRunAsync(string debugfs, string rawPath, string command, CancellationToken token)
    {
        var result = await ExternalProcess.RunAsync(debugfs, ["-R", command, rawPath], null, token);
        if (result.ExitCode != 0) throw new PatcherException("debugfs không đọc được image:\n" + result.CombinedOutput.Trim());
        return result.Stdout;
    }

    private static IReadOnlyDictionary<string, string> ParseProperties(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => !l.StartsWith('#')).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
    private static string? FirstValue(IReadOnlyDictionary<string, string> props, params string[] keys) => keys.Select(k => props.TryGetValue(k, out var value) ? value : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    private static int? ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    private static long ParseLong(string text, string pattern) => long.TryParse(Regex.Match(text, pattern, RegexOptions.IgnoreCase).Groups[1].Value, out var n) ? n : 0;
    private static IEnumerable<string> ProfileLines(PatchProfile profile) =>
        PatchProfileCatalog.Get(profile).RequiredInitLines;

    private static bool IsRarPath(string path) =>
        Path.GetExtension(path).Equals(".rar", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ExtractVendorFromRarAsync(string archivePath, string destination,
        IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        try
        {
            using var archive = RarArchive.OpenArchive(archivePath);
            var candidates = archive.Entries
                .Where(entry => !entry.IsDirectory)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .Where(entry => Regex.IsMatch(
                    Path.GetFileName(entry.Key!.Replace('\\', '/')),
                    @"^vendor(?:_[ab])?\.img$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToList();

            if (candidates.Count == 0)
                throw new PatcherException("RAR không chứa vendor.img, vendor_a.img hoặc vendor_b.img.");
            if (candidates.Count > 1)
                throw new PatcherException("RAR chứa nhiều vendor image; hãy chỉ giữ đúng image cần patch.");

            var entry = candidates[0];
            var entryKey = entry.Key!;
            if (!entry.IsComplete)
                throw new PatcherException("RAR nhiều phần chưa đầy đủ; cần cung cấp đủ các part của archive.");
            if (entry.Size <= 0)
                throw new PatcherException("Vendor image trong RAR rỗng hoặc không đọc được kích thước.");

            EnsureFreeSpace(Path.GetDirectoryName(destination) ?? Path.GetTempPath(),
                checked(entry.Size + 32L * 1024 * 1024));
            await using var input = await entry.OpenEntryStreamAsync(token);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 8 * 1024 * 1024,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            var buffer = new byte[8 * 1024 * 1024];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                progress?.Report(new(
                    7 + (int)(Math.Min(done, entry.Size) * 8 / Math.Max(1, entry.Size)),
                    $"Đang giải nén {Path.GetFileName(entryKey)}…"));
            }
            await output.FlushAsync(token);
            if (done != entry.Size)
                throw new PatcherException($"RAR giải nén thiếu dữ liệu (cần {entry.Size:N0}, nhận {done:N0} byte).");
            return entryKey;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PatcherException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PatcherException("Không thể đọc RAR: " + ex.Message);
        }
    }

    private static async Task CopyWithProgressAsync(string source, string destination, IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 8 * 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8 * 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[8 * 1024 * 1024]; long done = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), token); done += read; progress?.Report(new(5 + (int)(35 * done / Math.Max(1, input.Length)), "Sao chép image…")); }
        await output.FlushAsync(token);
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8 * 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var sha = SHA256.Create(); var hash = await sha.ComputeHashAsync(stream, token); return Convert.ToHexString(hash);
    }
    private static void EnsureFreeSpace(string directory, long required)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available < required) throw new PatcherException($"Không đủ dung lượng trên {root} (cần thêm khoảng {required:N0} byte, còn {available:N0}).");
        }
        catch (DriveNotFoundException) { }
    }
    private static void TryDelete(string? path) { if (!string.IsNullOrWhiteSpace(path)) try { File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string? path) { if (!string.IsNullOrWhiteSpace(path)) try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
