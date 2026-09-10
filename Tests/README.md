# Smoke/golden tests

`run-smoke.ps1` builds and runs the internal harness. It checks:

- F11 Android 9 stock is `Patchable` with the modern profile;
- F11 Android 10, Realme C2 fixed, and F7 API 27 fixed are `AlreadyPatched`;
- sparse converter raw/sparse round-trip;
- F11 patch creates a new image, leaves the stock SHA-256 unchanged, and re-analyzes as `AlreadyPatched`.

The harness uses the fixture images in the workspace and the same helper bundle as the release. To keep a generated golden output instead of a temporary file:

The OEM firmware fixtures are intentionally not included in the public repository. When they are absent, fixture-specific checks are reported as `SKIP`; the sparse round-trip check still runs. Place your own legally obtained fixtures in the workspace to enable the golden checks.

```powershell
$env:VOLTE_OUTPUT = '.\\F11 ANDROID 9\\vendor_F11_Android9_VoLTE_patched_v1.img'
dotnet run --project .\\Tests\\VoLTEVendorPatcher.Tests\\VoLTEVendorPatcher.Tests.csproj -c Release
Remove-Item Env:VOLTE_OUTPUT
```

For a sparse golden run, convert a fixture with the managed converter first and run the app against the resulting `.simg`; the same output verification path is used by the GUI.
