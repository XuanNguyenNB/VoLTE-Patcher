$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'VoLTEVendorPatcher.Tests/VoLTEVendorPatcher.Tests.csproj'
dotnet run --project $project -c Release --no-restore
