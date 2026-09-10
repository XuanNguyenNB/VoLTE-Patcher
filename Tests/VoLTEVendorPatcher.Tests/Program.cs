using System.Security.Cryptography;
using VoLTEVendorPatcher;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

if (args.Length > 0 && args[0].Equals("--ui-snapshot", StringComparison.OrdinalIgnoreCase))
{
    var outputPath = args.Length > 1
        ? Path.GetFullPath(args[1])
        : Path.Combine(root, "artifacts", "ui-snapshot.png");
    Exception? snapshotError = null;
    var snapshotThread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new MainForm
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000)
            };
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
            form.Close();
        }
        catch (Exception ex)
        {
            snapshotError = ex;
        }
    });
    snapshotThread.SetApartmentState(ApartmentState.STA);
    snapshotThread.Start();
    snapshotThread.Join();
    if (snapshotError != null)
    {
        Console.Error.WriteLine(snapshotError);
        return 1;
    }
    Console.WriteLine(outputPath);
    return 0;
}

var tools = Path.Combine(root, "VoLTEVendorPatcher", "Assets", "Tools");
if (Environment.GetEnvironmentVariable("VOLTE_TEST_EMBEDDED") != "1")
    Environment.SetEnvironmentVariable("VOLTE_PATCHER_TOOLS", tools);
var engine = new PatcherEngine();
var failures = new List<string>();

async Task Check(string relative, PatchState expected)
{
    var path = Path.Combine(root, relative);
    if (!File.Exists(path)) { Console.WriteLine($"SKIP missing private fixture: {relative}"); return; }
    try
    {
        var result = await engine.AnalyzeAsync(path, null, CancellationToken.None);
        if (result.State != expected) failures.Add($"{relative}: expected {expected}, got {result.State}");
        else Console.WriteLine($"PASS {relative}: {result.State} / {result.Profile}");
    }
    catch (Exception ex) { failures.Add($"{relative}: {ex.Message}"); }
}

await Check(Path.Combine("F11 ANDROID 9", "vendor.img"), PatchState.Patchable);
await Check(Path.Combine("F11_Android10_VENDOR", "vendor_F11_Android10_.img"), PatchState.AlreadyPatched);
await Check("vendor c2_VoLTE_fixed.img", PatchState.AlreadyPatched);
await Check("vendor_F7_CPH1859_VoLTE_android 8.img", PatchState.AlreadyPatched);

var sparseRoot = Path.Combine(Path.GetTempPath(), "volte-sparse-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sparseRoot);
try
{
    var rawFixture = Path.Combine(sparseRoot, "fixture.raw");
    var sparseFixture = Path.Combine(sparseRoot, "fixture.simg");
    var rawBytes = new byte[4096 * 6];
    Random.Shared.NextBytes(rawBytes.AsSpan(4096, 4096));
    Random.Shared.NextBytes(rawBytes.AsSpan(4096 * 4, 4096 * 2));
    await File.WriteAllBytesAsync(rawFixture, rawBytes);
    await SparseImage.ToSparseAsync(rawFixture, sparseFixture, 4096, null, CancellationToken.None);
    var detectedSparse = await SparseImage.DetectAsync(sparseFixture, CancellationToken.None);
    var roundTrip = Path.Combine(sparseRoot, "roundtrip.raw");
    await SparseImage.ToRawAsync(sparseFixture, roundTrip, detectedSparse.SparseHeader!.Value, null, CancellationToken.None);
    var originalHash = await PatcherEngine.HashFileAsync(rawFixture, CancellationToken.None);
    var roundTripHash = await PatcherEngine.HashFileAsync(roundTrip, CancellationToken.None);
    if (!originalHash.Equals(roundTripHash, StringComparison.OrdinalIgnoreCase)) failures.Add("sparse round-trip hash mismatch");
    else Console.WriteLine("PASS sparse converter round-trip");
}
catch (Exception ex) { failures.Add("sparse converter: " + ex.Message); }
finally { try { Directory.Delete(sparseRoot, true); } catch { } }

var stock = Path.Combine(root, "F11 ANDROID 9", "vendor.img");
if (File.Exists(stock))
{
    var before = await PatcherEngine.HashFileAsync(stock, CancellationToken.None);
    var requestedOutput = Environment.GetEnvironmentVariable("VOLTE_OUTPUT");
    var output = string.IsNullOrWhiteSpace(requestedOutput) ? Path.Combine(Path.GetTempPath(), "volte-smoke-" + Guid.NewGuid().ToString("N") + ".img") : Path.GetFullPath(requestedOutput);
    try
    {
        var analysis = await engine.AnalyzeAsync(stock, null, CancellationToken.None);
        var patched = await engine.PatchAsync(analysis, output, null, CancellationToken.None);
        var after = await PatcherEngine.HashFileAsync(stock, CancellationToken.None);
        if (!before.Equals(after, StringComparison.OrdinalIgnoreCase)) failures.Add("stock image hash changed");
        var verified = await engine.AnalyzeAsync(output, null, CancellationToken.None);
        if (verified.State != PatchState.AlreadyPatched) failures.Add("patched output did not re-analyze as AlreadyPatched");
        else Console.WriteLine($"PASS patch output: {patched.OutputHash}");
    }
    catch (Exception ex) { failures.Add("patch F11 Android 9: " + ex.Message); }
    finally { if (string.IsNullOrWhiteSpace(requestedOutput)) try { File.Delete(output); } catch { } }
}
else
{
    Console.WriteLine("SKIP F11 patch-output check: private stock fixture is not present.");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}
Console.WriteLine("All smoke tests passed.");
return 0;
