[CmdletBinding()]
param([switch]$RemoveLibraryEntries)
$ErrorActionPreference = 'Stop'
$sfRoot = Split-Path -Parent $PSScriptRoot
$sfCli = Join-Path $sfRoot 'App\SteamFusion.Cli.exe'
$sfData = Join-Path $env:LOCALAPPDATA 'SteamFusion'
& $sfCli exit
if ($LASTEXITCODE -ne 0) { throw 'Wait for or cancel the active operation before uninstalling.' }
if ($RemoveLibraryEntries) {
    & $sfCli remove-shortcuts
    if ($LASTEXITCODE -ne 0) { throw 'Close Steam before removing library entries.' }
}
$sfMarker = Join-Path $sfData 'installed-plugin.txt'
if (Test-Path $sfMarker) {
    if (Get-Process steam -ErrorAction SilentlyContinue) { throw 'Close Steam before removing its plugin.' }
    $sfPlugin = [IO.File]::ReadAllText($sfMarker).Trim()
    if ((Split-Path -Leaf $sfPlugin) -eq 'SteamFusion.star' -and (Test-Path $sfPlugin)) { Remove-Item -LiteralPath $sfPlugin }
    Remove-Item -LiteralPath $sfMarker
}
$sfLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'SteamFusion.lnk'
if (Test-Path $sfLink) { Remove-Item -LiteralPath $sfLink }
$sfPath = Join-Path $sfData 'cli-path.txt'
if (Test-Path $sfPath) { Remove-Item -LiteralPath $sfPath }
Write-Host 'Registration removed. Configuration, saves, Sandboxie boxes and VDF backups were preserved.'
Write-Host 'Remove SteamFusion entries from Steam before deleting this application folder.'
