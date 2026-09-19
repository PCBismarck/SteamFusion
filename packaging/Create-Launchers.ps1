[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sfRoot = Split-Path -Parent $PSScriptRoot
$sfFolder = Join-Path $PSScriptRoot 'Launchers'
New-Item -ItemType Directory -Path $sfFolder -Force | Out-Null
$sfShell = New-Object -ComObject WScript.Shell
$sfEntries = @{
    'SteamFusion' = (Join-Path $sfRoot 'App\SteamFusion.exe')
    'Small Steam' = (Join-Path $PSScriptRoot 'Open-Small-Steam.cmd')
    'Main Steam' = (Join-Path $PSScriptRoot 'Open-Main-Steam.cmd')
}
foreach ($sfName in $sfEntries.Keys) {
    if (!(Test-Path $sfEntries[$sfName])) { throw ('Missing launcher target: ' + $sfEntries[$sfName]) }
    $sfLinkFolder = if ($sfName -eq 'SteamFusion') { $sfRoot } else { $sfFolder }
    $sfLink = $sfShell.CreateShortcut((Join-Path $sfLinkFolder ($sfName + '.lnk')))
    $sfLink.TargetPath = $sfEntries[$sfName]
    $sfLink.WorkingDirectory = $sfRoot
    if ($sfName -eq 'SteamFusion') { $sfLink.IconLocation = (Join-Path $sfRoot 'App\Assets\SteamFusion.ico') + ',0' }
    $sfLink.Save()
}
Write-Host ('Daily shortcuts: ' + $sfFolder)
