[CmdletBinding()]
param(
    [string]$CygwinRoot = (Join-Path $PSScriptRoot '.cache/cygwin-x64'),
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $PSScriptRoot 'tools.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$cache = Join-Path $PSScriptRoot 'cache'
$assetDir = Join-Path $repo 'VoLTEVendorPatcher/Assets/Tools'
New-Item -ItemType Directory -Force -Path $cache, $assetDir | Out-Null

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-PinnedFile([string]$Url, [string]$Path, [string]$Sha256) {
    if (Test-Path -LiteralPath $Path) {
        $actual = Get-Sha256 $Path
        if ($actual -ne $Sha256.ToUpperInvariant()) {
            throw "Hash mismatch: $Path (expected $Sha256, got $actual). Remove the incomplete file and retry."
        }
        return
    }
    $part = "$Path.$([Guid]::NewGuid().ToString('N')).part"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $part -UseBasicParsing
        $actual = Get-Sha256 $part
        if ($actual -ne $Sha256.ToUpperInvariant()) { throw "Hash mismatch while downloading $Url (got $actual)." }
        [IO.File]::Move($part, $Path)
    }
    finally { if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force } }
}

Write-Host 'Downloading and verifying pinned source archives...'
Get-PinnedFile $lock.e2fsprogsSource.url (Join-Path $cache 'e2fsprogs-1.47.4.tar.gz') $lock.e2fsprogsSource.sha256
Get-PinnedFile $lock.libsparseSource.url (Join-Path $cache 'libsparse-main.tar.gz') $lock.libsparseSource.sha256

$setupPath = Join-Path $cache 'setup-x86_64.exe'
Get-PinnedFile $lock.cygwinSetup.url $setupPath $lock.cygwinSetup.sha256
$debugfs = Join-Path $CygwinRoot 'usr/sbin/debugfs.exe'
$e2fsck = Join-Path $CygwinRoot 'usr/sbin/e2fsck.exe'
if ($Refresh -or -not (Test-Path -LiteralPath $debugfs) -or -not (Test-Path -LiteralPath $e2fsck)) {
    New-Item -ItemType Directory -Force -Path $CygwinRoot | Out-Null
    $setupArgs = @('-q', '-B', '-A', 'x86_64', '-R', $CygwinRoot, '-s', $lock.cygwinE2fsprogs.mirror, '-P', 'e2fsprogs', '-l', (Join-Path $cache 'cyg-download'))
    $setupArgsForProcess = $setupArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\\"') + '"' } else { $_ } }
    Write-Host "Installing Cygwin e2fsprogs into $CygwinRoot ..."
    $proc = Start-Process -FilePath $setupPath -ArgumentList $setupArgsForProcess -Wait -PassThru -NoNewWindow
    if ($proc.ExitCode -ne 0) { throw "Cygwin setup failed, exit $($proc.ExitCode)." }
}
if (-not (Test-Path -LiteralPath $debugfs) -or -not (Test-Path -LiteralPath $e2fsck)) {
    throw 'debugfs.exe/e2fsck.exe not found after Cygwin setup.'
}

$binDir = Join-Path $CygwinRoot 'bin'
$runtimeFiles = @('debugfs.exe', 'e2fsck.exe', 'cygwin1.dll', 'cygblkid-1.dll', 'cygcom_err-2.dll', 'cyge2p-2.dll', 'cygext2fs-2.dll', 'cyggcc_s-seh-1.dll', 'cygiconv-2.dll', 'cygintl-8.dll', 'cygss-2.dll', 'cyguuid-1.dll')
foreach ($name in $runtimeFiles) {
    $source = if ($name -match '\.exe$') { Join-Path (Join-Path $CygwinRoot 'usr/sbin') $name } else { Join-Path $binDir $name }
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing Cygwin dependency: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $assetDir $name) -Force
}
Copy-Item -LiteralPath (Join-Path $CygwinRoot 'usr/share/doc/e2fsprogs/NOTICE') -Destination (Join-Path $assetDir 'e2fsprogs-NOTICE.txt') -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $CygwinRoot 'usr/share/doc/Cygwin/COPYING') -Destination (Join-Path $assetDir 'Cygwin-COPYING.txt') -Force -ErrorAction SilentlyContinue

$oldPath = $env:PATH
$env:PATH = "$binDir;$oldPath"
$versionOut = Join-Path $cache 'debugfs-version.out'
$versionErr = Join-Path $cache 'debugfs-version.err'
$versionProc = Start-Process -FilePath $debugfs -ArgumentList @('-V') -Wait -PassThru -NoNewWindow -RedirectStandardOutput $versionOut -RedirectStandardError $versionErr
$versionText = ((Get-Content -LiteralPath $versionOut -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath $versionErr -Raw -ErrorAction SilentlyContinue)).Trim()
Remove-Item -LiteralPath $versionOut, $versionErr -Force -ErrorAction SilentlyContinue
$env:PATH = $oldPath
if ($versionProc.ExitCode -ne 0) { throw "debugfs -V failed, exit $($versionProc.ExitCode)." }
if ($versionText -notmatch [regex]::Escape($lock.cygwinE2fsprogs.version.Split('-')[0])) { throw "debugfs version does not match lock: $versionText" }
$files = [ordered]@{}
foreach ($name in $runtimeFiles) { $files[$name] = Get-Sha256 (Join-Path $assetDir $name) }
$manifest = [ordered]@{ bundleVersion = "cygwin-e2fsprogs-$($lock.cygwinE2fsprogs.version)"; source = $lock.cygwinE2fsprogs.source; files = $files }
$json = $manifest | ConvertTo-Json -Depth 5
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $assetDir 'tools.lock.json'), $json, $utf8)
Write-Host "Embedded helper bundle ready: $assetDir"
Write-Host $versionText
