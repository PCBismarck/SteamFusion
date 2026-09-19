[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ExpectedBox,
      [string]$ReleaseDirectory = 'F:\SteamFusion')
$ErrorActionPreference = 'Stop'
$sfReport = [ordered]@{ expectedBox = $ExpectedBox; actualBox = ''; pipeConnected = $false; error = '' }
try {
    Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SFBoxProbe {
    [DllImport("SbieDll.dll", CharSet=CharSet.Unicode)]
    public static extern int SbieApi_QueryProcess(IntPtr pid, StringBuilder box, StringBuilder image, StringBuilder sid, out uint session);
}
'@
    $sfBox = New-Object Text.StringBuilder(34)
    $sfImage = New-Object Text.StringBuilder(96)
    $sfSid = New-Object Text.StringBuilder(96)
    [uint32]$sfSession = 0
    $sfStatus = [SFBoxProbe]::SbieApi_QueryProcess([IntPtr]$PID, $sfBox, $sfImage, $sfSid, [ref]$sfSession)
    if ($sfStatus -ne 0) { throw ('Process query status: ' + $sfStatus) }
    $sfReport.actualBox = $sfBox.ToString()
    if ($sfReport.actualBox -ne $ExpectedBox) { throw 'Sandbox identity mismatch.' }
    $sfReplyText = (& (Join-Path $ReleaseDirectory 'App\SteamFusion.Cli.exe') status | Out-String)
    if ($LASTEXITCODE -ne 0) { throw ('Controller request failed: ' + $sfReplyText) }
    $sfReply = $sfReplyText | ConvertFrom-Json
    if (!$sfReply.success) { throw $sfReply.message }
    $sfReport.pipeConnected = $true
    $sfReport.controllerState = $sfReply.code
} catch { $sfReport.error = $_.Exception.Message }
$sfOutput = Join-Path $env:LOCALAPPDATA ('SteamFusion\signals\' + $ExpectedBox + '.json.probe')
[IO.File]::WriteAllText($sfOutput, ($sfReport | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
if ($sfReport.error) { exit 1 }
exit 0
