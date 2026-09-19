[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$LibraryRoot, [switch]$Disable)
$ErrorActionPreference='Stop'
$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$sfRoot=Split-Path -Parent $PSScriptRoot
$sfState=Join-Path $env:LOCALAPPDATA 'SteamFusion'
$sfConfigPath=Join-Path $sfState 'config.json'
$sfConfig=Get-Content -LiteralPath $sfConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$sfLibrary=[IO.Path]::GetFullPath($LibraryRoot).TrimEnd('\')
$sfApps=Join-Path $sfLibrary 'steamapps'
if(!(Test-Path -LiteralPath $sfApps -PathType Container)){throw 'Choose an existing Steam library containing steamapps.'}
if($sfLibrary -notmatch '^[A-Za-z]:\\' -or $sfLibrary -match '[*,\r\n]'){throw 'Only a local library path without wildcards is supported.'}
$sfStart=$sfConfig.sandboxieStartExe
$sfIni=Join-Path (Split-Path $sfStart) 'SbieIni.exe'
$sfIniPath=Join-Path $env:WINDIR 'Sandboxie.ini'
if(!(Test-Path $sfIni)){throw 'Sandboxie configuration tool is missing.'}
$sfStatus=(& (Join-Path $sfRoot 'SteamFusion.Cli.exe') status | Out-String | ConvertFrom-Json)
if($sfStatus.code -eq 'busy'){throw 'Wait for the current SteamFusion operation to finish.'}
$sfBackup=Join-Path $sfRoot ('Backups\shared-library-'+(Get-Date -Format 'yyyyMMdd-HHmmssfff'))
New-Item -ItemType Directory -Path $sfBackup -Force | Out-Null
Copy-Item -LiteralPath $sfIniPath -Destination (Join-Path $sfBackup 'Sandboxie.ini')
Copy-Item -LiteralPath $sfConfigPath -Destination (Join-Path $sfBackup 'config.json')
$sfRule='steam.exe,'+$sfApps+'\'
foreach($sfAccount in $sfConfig.accounts){
 if($sfAccount.sandboxName -notmatch '^[A-Za-z][A-Za-z0-9_]{0,31}$'){throw 'Invalid sandbox name.'}
 $sfRules=@(& $sfIni query $sfAccount.sandboxName OpenFilePath)
 if($Disable -and $sfRules -contains $sfRule){ & $sfIni delete $sfAccount.sandboxName OpenFilePath $sfRule }
 elseif(!$Disable -and $sfRules -notcontains $sfRule){ & $sfIni append $sfAccount.sandboxName OpenFilePath $sfRule }
 else {continue}
 if($LASTEXITCODE -ne 0){throw 'Sandboxie rejected the shared-library rule.'}
}
& $sfStart /reload
if($LASTEXITCODE -ne 0){throw 'Sandboxie reload failed.'}
foreach($sfAccount in $sfConfig.accounts){
 $sfRules=@(& $sfIni query $sfAccount.sandboxName OpenFilePath)
 if(($sfRules -contains $sfRule) -eq [bool]$Disable){throw 'Shared-library rule verification failed.'}
}
$sfConfig | Add-Member -NotePropertyName sharedLibraryDownloads -NotePropertyValue (!$Disable) -Force
$sfTemp=$sfConfigPath+'.shared.tmp'
[IO.File]::WriteAllText($sfTemp,($sfConfig | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
[IO.File]::Replace($sfTemp,$sfConfigPath,$sfConfigPath+'.bak')
[IO.File]::WriteAllText((Join-Path $sfState 'shared-library.json'),(@{library=$sfLibrary;enabled=(!$Disable);rule=$sfRule;backup=$sfBackup;at=(Get-Date).ToString('o')} | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
Write-Output ('Library: '+$sfLibrary)
Write-Output ('Steam-only direct write: '+(!$Disable))
Write-Output ('Backup: '+$sfBackup)
Write-Output 'Restart sandbox Steam before downloading. Only one client may update/uninstall the same game at a time.'
