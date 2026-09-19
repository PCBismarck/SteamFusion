[CmdletBinding()]
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts\win-x64' }
$sfSource = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sfOutput = [IO.Path]::GetFullPath($OutputDirectory)
Push-Location $sfSource
try {
    & dotnet run --project tests/SteamFusion.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    foreach ($sfProject in @('SteamFusion.App', 'SteamFusion.Cli')) {
        & dotnet publish ('src/' + $sfProject) -c Release -r win-x64 --self-contained true -o (Join-Path $sfOutput 'App') --nologo
        if ($LASTEXITCODE -ne 0) { throw ('Publish failed: ' + $sfProject) }
    }
    Push-Location integrations/millennium
    try {
        & npm.cmd ci --ignore-scripts
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
        & npm.cmd test
        if ($LASTEXITCODE -ne 0) { throw 'Plugin tests failed.' }
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    } finally { Pop-Location }
    New-Item -ItemType Directory -Path (Join-Path $sfOutput 'Scripts'), (Join-Path $sfOutput 'Docs') -Force | Out-Null
    Copy-Item packaging/* (Join-Path $sfOutput 'Scripts') -Force
    Copy-Item packaging/START-HERE.txt (Join-Path $sfOutput '开始使用.txt') -Force
    Remove-Item (Join-Path $sfOutput 'Scripts\START-HERE.txt')
    Copy-Item README.md,VERIFICATION.md (Join-Path $sfOutput 'Docs') -Force
    & (Join-Path $sfOutput 'Scripts\Create-Launchers.ps1')
    New-Item -ItemType Directory -Path (Join-Path $sfOutput 'App\Millennium') -Force | Out-Null
    Copy-Item integrations/millennium/SteamFusion.star (Join-Path $sfOutput 'App\Millennium') -Force
    Write-Host ('Release ready: ' + $sfOutput)
} finally { Pop-Location }
