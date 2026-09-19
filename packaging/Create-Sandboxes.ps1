[CmdletBinding()]
param([string]$DataRoot = '',
      [string]$SandboxieIni = (Join-Path $env:WINDIR 'Sandboxie.ini'))
$ErrorActionPreference = 'Stop'
$sfRoot = Split-Path -Parent $PSScriptRoot
if (!$DataRoot) { $DataRoot = Join-Path $sfRoot 'Sandboxes' }
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$sfState = Join-Path $env:LOCALAPPDATA 'SteamFusion'
$sfConfig = Get-Content (Join-Path $sfState 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$sfCli = Join-Path $sfRoot 'SteamFusion.Cli.exe'
$sfStart = $sfConfig.sandboxieStartExe
$sfIniTool = Join-Path (Split-Path $sfStart) 'SbieIni.exe'
if (!(Test-Path $sfStart) -or !(Test-Path $sfIniTool)) { throw 'Set the installed Sandboxie Start.exe path in SteamFusion first.' }
if (!(Test-Path $SandboxieIni)) { throw 'Specify the active Sandboxie.ini path with -SandboxieIni.' }
$sfPipe = (& $sfCli pipe-name | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sfPipe -notmatch '^SteamFusion-[A-F0-9]{20}$') { throw 'Cannot identify controller pipe.' }
$sfBefore = [IO.File]::ReadAllText($SandboxieIni)
foreach ($sfAccount in $sfConfig.accounts) {
    if ($sfAccount.sandboxName -notmatch '^[A-Za-z][A-Za-z0-9_]{0,31}$') { throw 'Invalid sandbox name.' }
}
$sfBackups = Join-Path $sfRoot 'Backups'
New-Item -ItemType Directory -Path $sfBackups -Force | Out-Null
$sfBackup = Join-Path $sfBackups ('Sandboxie-before-create-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '.ini')
Copy-Item -LiteralPath $SandboxieIni -Destination $sfBackup
Write-Host ('Backup: ' + $sfBackup)
function Set-SFValue([string]$Box, [string]$Key, [string]$Value) {
    & $sfIniTool set $Box $Key $Value
    if ($LASTEXITCODE -ne 0) { throw ('Sandboxie rejected setting ' + $Box + '/' + $Key + '. Check its configuration permissions.') }
}
function Add-SFValue([string]$Box, [string]$Key, [string]$Value) {
    $sfCurrent = @(& $sfIniTool query $Box $Key)
    if ($sfCurrent -notcontains $Value) {
        & $sfIniTool append $Box $Key $Value
        if ($LASTEXITCODE -ne 0) { throw ('Sandboxie rejected setting ' + $Box + '/' + $Key) }
    }
}
New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
foreach ($sfAccount in $sfConfig.accounts) {
    $sfBox = $sfAccount.sandboxName
    $sfExists = $sfBefore -match ('(?m)^\[' + [Regex]::Escape($sfBox) + '\]\s*$')
    if (!$sfExists) {
        Set-SFValue $sfBox 'Enabled' 'y'
        Set-SFValue $sfBox 'ConfigLevel' '10'
        Set-SFValue $sfBox 'BoxAlias' $sfAccount.name
        Set-SFValue $sfBox 'BoxNameTitle' 'y'
        Set-SFValue $sfBox 'FileRootPath' (Join-Path ([IO.Path]::GetFullPath($DataRoot)) '%USER%\%SANDBOX%')
        Set-SFValue $sfBox 'BlockNetworkFiles' 'y'
        Set-SFValue $sfBox 'AutoRecover' 'n'
        $sfColor = if ($sfAccount.id -eq $sfConfig.defaultNativeAccountId) { '#57C785,ttl' } else { '#4FA3FF,ttl' }
        Set-SFValue $sfBox 'BorderColor' $sfColor
        foreach ($sfTemplate in @('AutoRecoverIgnore','LingerPrograms','BlockPorts','qWave','FileCopy','SkipHook','OpenBluetooth')) {
            Add-SFValue $sfBox 'Template' $sfTemplate
        }
    }
    Set-SFValue $sfBox 'AutoDelete' 'n'
    Add-SFValue $sfBox 'OpenPipePath' ('SteamFusion.Cli.exe,\Device\NamedPipe\' + $sfPipe)
    Add-SFValue $sfBox 'OpenPipePath' (Join-Path $sfState ('signals\' + $sfBox + '.json*'))
    $sfSteamRoot = Split-Path -Parent $sfConfig.steamExe
    if ((Test-Path (Join-Path $sfSteamRoot 'millennium\lib\millennium.dll')) -and
        (Test-Path (Join-Path $sfSteamRoot 'wsock32.dll'))) {
        # Millennium 3.4.1 aborts inside the tested Sandboxie environment.
        # Hide only its Steam-local loader in these boxes; Windows resolves the system WSOCK32.
        Add-SFValue $sfBox 'ClosedFilePath' ('steam.exe,' + (Join-Path $sfSteamRoot 'wsock32.dll'))
    }
    Write-Host (($sfAccount.name) + ' -> ' + $sfBox)
}
& $sfStart /reload
if ($LASTEXITCODE -ne 0) { throw 'Sandboxie configuration reload failed.' }
foreach ($sfAccount in $sfConfig.accounts) {
    $sfEnabled = (& $sfIniTool query $sfAccount.sandboxName Enabled | Out-String).Trim()
    if ($sfEnabled -ne 'y') { throw ('Sandbox is not enabled: ' + $sfAccount.sandboxName) }
}
Write-Host 'Persistent sandboxes configured. Existing boxes and Steam credentials were preserved.'
