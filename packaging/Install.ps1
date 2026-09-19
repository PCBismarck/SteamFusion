[CmdletBinding()]
param([switch]$Startup, [switch]$NoStart)
$ErrorActionPreference = 'Stop'
$sfRoot = Split-Path -Parent $PSScriptRoot
$sfCli = Join-Path $sfRoot 'App\SteamFusion.Cli.exe'
$sfApp = Join-Path $sfRoot 'App\SteamFusion.exe'
$sfData = Join-Path $env:LOCALAPPDATA 'SteamFusion'
$sfUtf8 = New-Object System.Text.UTF8Encoding($false)
if (!(Test-Path $sfApp) -or !(Test-Path $sfCli)) { throw 'Keep this script in the Scripts subfolder of the complete Windows release.' }
New-Item -ItemType Directory -Force -Path (Join-Path $sfData 'signals') | Out-Null
[IO.File]::WriteAllText((Join-Path $sfData 'cli-path.txt'), $sfCli, $sfUtf8)
if (!(Test-Path (Join-Path $sfData 'config.json'))) {
    & $sfCli init
    if ($LASTEXITCODE -ne 0) { throw 'Configuration initialization failed.' }
}
& $sfCli refresh-plugin
if ($LASTEXITCODE -ne 0) { throw 'Plugin configuration update failed.' }
$sfConfig = Get-Content (Join-Path $sfData 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$sfSteam = Split-Path -Parent $sfConfig.steamExe
$sfPlugin = Join-Path $sfSteam 'millennium\plugins\SteamFusion.star'
if (Test-Path (Join-Path $sfSteam 'millennium\bin')) {
    if (Get-Process steam -ErrorAction SilentlyContinue) { throw 'Exit Steam normally, then rerun Install.ps1 to install the plugin.' }
    New-Item -ItemType Directory -Force -Path (Split-Path $sfPlugin) | Out-Null
    if (Test-Path $sfPlugin) { Copy-Item $sfPlugin ($sfPlugin + '.bak') -Force }
    Copy-Item (Join-Path $sfRoot 'App\Millennium\SteamFusion.star') $sfPlugin -Force
    [IO.File]::WriteAllText((Join-Path $sfData 'installed-plugin.txt'), $sfPlugin, $sfUtf8)
    Write-Host ('Plugin installed: ' + $sfPlugin)
} else {
    Write-Host 'Millennium is not installed. Install it, exit Steam, then rerun Install.cmd.'
}
$sfPipe = (& $sfCli pipe-name | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sfPipe -notmatch '^SteamFusion-[A-F0-9]{20}$') { throw 'Cannot identify the user pipe.' }
$sfLines = @('; Generated for this Windows user. See Docs/README.md before applying.', '; Keep each box persistent and log in to its assigned account.', '')
foreach ($sfAccount in $sfConfig.accounts) {
    $sfLines += '[' + $sfAccount.sandboxName + ']'
    $sfLines += 'Enabled=y'
    $sfLines += 'AutoDelete=n'
    $sfLines += 'OpenPipePath=SteamFusion.Cli.exe,\Device\NamedPipe\' + $sfPipe
    $sfLines += 'OpenPipePath=' + (Join-Path $sfData ('signals\' + $sfAccount.sandboxName + '.json*'))
    $sfLines += ''
}
[IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'Sandboxie.generated.ini'), $sfLines, $sfUtf8)
& (Join-Path $PSScriptRoot 'Register-Paths.ps1')
if ($Startup) {
    $sfLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'SteamFusion.lnk'
    $sfShell = New-Object -ComObject WScript.Shell
    $sfShortcut = $sfShell.CreateShortcut($sfLink)
    $sfShortcut.TargetPath = $sfApp; $sfShortcut.Arguments = '--agent'; $sfShortcut.WorkingDirectory = $sfRoot
    $sfShortcut.IconLocation = (Join-Path $sfRoot 'App\Assets\SteamFusion.ico') + ',0'
    $sfShortcut.Save()
    Write-Host 'Background controller will start at Windows sign-in.'
}
Write-Host ('Configuration: ' + $sfData)
Write-Host 'Open the root SteamFusion shortcut to check accounts and game routes.'
if (!$NoStart) { Start-Process -FilePath $sfApp }
