[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipToolBootstrap,
    [string]$SignCertificate = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'VoLTEVendorPatcher/VoLTEVendorPatcher.csproj'
$publishDir = Join-Path $repo "artifacts/$Configuration/win-x64"

if (-not $SkipToolBootstrap) {
    & (Join-Path $PSScriptRoot 'bootstrap-tools.ps1')
}

& dotnet restore $project
& dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed, exit $LASTEXITCODE." }

$pdb = Join-Path $publishDir 'VoLTEVendorPatcher.pdb'
if (Test-Path -LiteralPath $pdb) { Remove-Item -LiteralPath $pdb -Force }

$exe = Join-Path $publishDir 'VoLTEVendorPatcher.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "EXE not found after publish: $exe" }

if (-not [string]::IsNullOrWhiteSpace($SignCertificate)) {
    $signtool = if ($env:SIGNTOOL) { $env:SIGNTOOL } else { 'signtool.exe' }
    & $signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f $SignCertificate $exe
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed, exit $LASTEXITCODE." }
}

$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToUpperInvariant()
$line = "$hash  $([IO.Path]::GetFileName($exe))"
[IO.File]::WriteAllText((Join-Path $publishDir 'SHA256SUMS.txt'), $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Published: $exe"
Write-Host "SHA-256: $hash"
if ([string]::IsNullOrWhiteSpace($SignCertificate)) { Write-Host 'No Authenticode certificate supplied; publish SHA-256 with the release.' }
