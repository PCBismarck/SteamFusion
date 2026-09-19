[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AccountId)
$ErrorActionPreference = 'Stop'
$sfRoot = Split-Path -Parent $PSScriptRoot
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$sfConfig = Get-Content (Join-Path $env:LOCALAPPDATA 'SteamFusion\config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$sfAccount = $sfConfig.accounts | Where-Object id -eq $AccountId | Select-Object -First 1
if (!$sfAccount -or $sfAccount.sandboxName -notmatch '^[A-Za-z][A-Za-z0-9_]{0,31}$') { throw 'Invalid account or sandbox configuration.' }
if (!(Test-Path $sfConfig.sandboxieStartExe) -or !(Test-Path $sfConfig.steamExe)) { throw 'Check Steam and Sandboxie paths in SteamFusion.' }
& (Join-Path $sfRoot 'SteamFusion.Cli.exe') status
if ($LASTEXITCODE -ne 0) { throw 'Cannot start the ordinary Windows controller.' }
Write-Host ('Open ' + $sfAccount.name + ' in ' + $sfAccount.sandboxName)
Write-Host 'This launcher selects a sandbox, not the Steam login. On first use, select the intended account inside Steam.'
& $sfConfig.sandboxieStartExe ('/box:' + $sfAccount.sandboxName) $sfConfig.steamExe
if ($LASTEXITCODE -ne 0) { throw 'Sandboxie could not start Steam.' }
Write-Host 'Checking that the Steam process stays running in the target sandbox...'
$sfDeadline = [DateTime]::UtcNow.AddSeconds(20)
$sfSeenAt = $null
$sfSeenProcess = ''
do {
    Start-Sleep -Seconds 1
    $sfStateJson = (& (Join-Path $sfRoot 'SteamFusion.Cli.exe') probe | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect Steam processes. Open SteamFusion settings for details.' }
    $sfState = $sfStateJson | ConvertFrom-Json
    $sfInstance = $sfState.instances | Where-Object environment -eq $sfAccount.sandboxName | Select-Object -First 1
    if ($sfInstance) {
        $sfProcessKey = [string]$sfInstance.pid + ':' + [string]$sfInstance.startTicks
        if (!$sfSeenAt -or $sfSeenProcess -ne $sfProcessKey) { $sfSeenAt = [DateTime]::UtcNow; $sfSeenProcess = $sfProcessKey }
        if (([DateTime]::UtcNow - $sfSeenAt).TotalSeconds -ge 8) {
            Write-Host ('Steam process remains running in ' + $sfAccount.sandboxName + '. Check the Steam window and its login account.')
            exit 0
        }
    } else { $sfSeenAt = $null }
} while ([DateTime]::UtcNow -lt $sfDeadline)
throw 'Steam did not stay running during the startup check. Inspect Sandboxie/Steam messages; an update may still be in progress. See Docs\VERIFICATION.md for the Millennium workaround.'
