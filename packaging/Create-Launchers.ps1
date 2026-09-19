[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sfRoot = Split-Path -Parent $PSScriptRoot
$sfFolder = Join-Path $sfRoot 'Launchers'
New-Item -ItemType Directory -Path $sfFolder -Force | Out-Null
$sfShell = New-Object -ComObject WScript.Shell
$sfEntries = @{
    'SteamFusion' = (Join-Path $sfRoot 'SteamFusion.exe')
    'Small Steam' = (Join-Path $PSScriptRoot 'Open-Small-Steam.cmd')
    'Main Steam' = (Join-Path $PSScriptRoot 'Open-Main-Steam.cmd')
}
foreach ($sfName in $sfEntries.Keys) {
    if (!(Test-Path $sfEntries[$sfName])) { throw ('Missing launcher target: ' + $sfEntries[$sfName]) }
    $sfLink = $sfShell.CreateShortcut((Join-Path $sfFolder ($sfName + '.lnk')))
    $sfLink.TargetPath = $sfEntries[$sfName]
    $sfLink.WorkingDirectory = $sfRoot
    if ($sfName -eq 'SteamFusion') { $sfLink.IconLocation = (Join-Path $sfRoot 'Assets\SteamFusion.ico') + ',0' }
    $sfLink.Save()
}
Write-Host ('Daily shortcuts: ' + $sfFolder)
