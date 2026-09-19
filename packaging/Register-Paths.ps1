[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sfRoot = Split-Path -Parent $PSScriptRoot
$sfApp = Join-Path $sfRoot 'App\SteamFusion.exe'
$sfCli = Join-Path $sfRoot 'App\SteamFusion.Cli.exe'
if (!(Test-Path $sfApp) -or !(Test-Path $sfCli)) { throw 'The complete release must include App/SteamFusion.exe and App/SteamFusion.Cli.exe.' }
$sfData = Join-Path $env:LOCALAPPDATA 'SteamFusion'
New-Item -ItemType Directory -Path $sfData -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $sfData 'cli-path.txt'), $sfCli, (New-Object Text.UTF8Encoding($false)))
if (Test-Path (Join-Path $sfData 'config.json')) {
    & $sfCli refresh-plugin
    if ($LASTEXITCODE -ne 0) { throw 'Plugin configuration update failed.' }
}
# Repair existing user shortcuts only; do not enable startup for users who did not request it.
$sfShell = New-Object -ComObject WScript.Shell
$sfFolders = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Startup'), (Join-Path $PSScriptRoot 'Launchers'))
foreach ($sfFolder in $sfFolders) {
    if (!$sfFolder -or !(Test-Path $sfFolder)) { continue }
    foreach ($sfFile in Get-ChildItem -LiteralPath $sfFolder -Filter '*.lnk' -File) {
        $sfLink = $sfShell.CreateShortcut($sfFile.FullName)
        if (!$sfLink.TargetPath) { continue }
        if ((Split-Path -Leaf $sfLink.TargetPath) -ne 'SteamFusion.exe') { continue }
        $sfLink.TargetPath = $sfApp
        $sfLink.WorkingDirectory = $sfRoot
        $sfLink.IconLocation = (Join-Path $sfRoot 'App\Assets\SteamFusion.ico') + ',0'
        $sfLink.Save()
    }
}
& (Join-Path $PSScriptRoot 'Create-Launchers.ps1')
Write-Host 'Plugin path and application shortcuts updated.'
