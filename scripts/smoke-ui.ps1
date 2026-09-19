[CmdletBinding()]
param([string]$ReleaseDirectory = 'F:\SteamFusion', [string]$Screenshot = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$sfProcess = Get-Process SteamFusion | Select-Object -First 1
$sfCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $sfProcess.Id)
$sfWindow = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $sfCondition)
if (!$sfWindow -or !$sfWindow.Current.Name.Contains('SteamFusion')) { throw 'Settings window not found.' }
$sfAll = $sfWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$sfButtons = @($sfAll | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' })
if ($sfButtons.Count -lt 8) { throw 'Settings controls did not load.' }
Write-Host ('PASS settings window and ' + $sfButtons.Count + ' buttons; PID=' + $sfProcess.Id)
if ($Screenshot) {
    Add-Type -AssemblyName System.Drawing
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SFWindowCapture {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left, top, right, bottom; }
}
'@
    $sfHandle = [IntPtr]$sfWindow.Current.NativeWindowHandle
    $sfRect = New-Object SFWindowCapture+Rect
    [SFWindowCapture]::GetWindowRect($sfHandle, [ref]$sfRect) | Out-Null
    $sfBitmap = New-Object Drawing.Bitmap(($sfRect.right-$sfRect.left), ($sfRect.bottom-$sfRect.top))
    $sfGraphics = [Drawing.Graphics]::FromImage($sfBitmap)
    $sfDc = $sfGraphics.GetHdc()
    try { [SFWindowCapture]::PrintWindow($sfHandle, $sfDc, 2) | Out-Null }
    finally { $sfGraphics.ReleaseHdc($sfDc); $sfGraphics.Dispose() }
    $sfBitmap.Save($Screenshot, [Drawing.Imaging.ImageFormat]::Png); $sfBitmap.Dispose()
}
$sfPattern = $sfWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
$sfPattern.Close()
& (Join-Path $ReleaseDirectory 'SteamFusion.Cli.exe') status
if ($LASTEXITCODE -ne 0) { throw 'Controller stopped when settings window closed.' }
Write-Host 'PASS controller survives settings window close.'
