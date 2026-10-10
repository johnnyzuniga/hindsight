<#
  Records the Hindsight demo: drives the real app through UI Automation while FFmpeg captures the screen.
  Leave the PC alone while it runs (about 4 minutes); turn on Do Not Disturb so notifications don't appear.

    powershell -ExecutionPolicy Bypass -File tools\video\record.ps1

  Output: dist\video\raw.mkv and dist\video\marks.json (timestamps used by edit.ps1).
#>
param([switch]$Tail)   # -Tail records only replay, narration and the Local AI page (to patch a run)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$out = Join-Path $root 'dist\video'
New-Item -ItemType Directory -Force $out | Out-Null
function Find-Tool([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $link = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\$name.exe"
    if (Test-Path $link) { return $link }
    $pkg = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Directory -Filter 'Gyan.FFmpeg*' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter "$name.exe" } | Select-Object -First 1
    if ($pkg) { return $pkg.FullName }
    throw "$name not found. Install it with: winget install Gyan.FFmpeg"
}$ff = Find-Tool 'ffmpeg'
$app = Join-Path $root 'src\ResMan.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\ResMan.App.exe'
$cli = Join-Path $root 'src\ResMan.Cli\bin\Debug\net10.0-windows10.0.19041.0\resman.exe'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
}
"@
[Win]::SetProcessDPIAware() | Out-Null
$A = [System.Windows.Automation.AutomationElement]

# ---- backend ----
if (-not (Get-Process ResMan.Service -ErrorAction SilentlyContinue)) { Start-Process dotnet -ArgumentList "run --project `"$root\src\ResMan.Service`" --no-build" -WindowStyle Minimized }
if (-not (Get-Process ResMan.Agent -ErrorAction SilentlyContinue)) { Start-Process dotnet -ArgumentList "run --project `"$root\src\ResMan.Agent`" --no-build" -WindowStyle Minimized; Start-Sleep 12 }

# ---- the window: light theme, 1920x1080 at the top-left of the screen ----
Get-Process ResMan.App -ErrorAction SilentlyContinue | Stop-Process -Force
$startArgs = '--page overview --theme light'
if ($Tail) {
    $info = Get-Content (Join-Path $env:LOCALAPPDATA 'ResMan\agent.json') | ConvertFrom-Json
    $tl = Invoke-RestMethod "$($info.url)/api/v1/timeline?days=1" -Headers @{ Authorization = "Bearer $($info.token)" }
    $freeze = ($tl | Where-Object { $_.kind -eq 'app_hang' } | Select-Object -First 1).eventIds[0]
    $startArgs = "--event $freeze --theme light"
}
Start-Process $app -ArgumentList $startArgs
Start-Sleep 8
$h = (Get-Process ResMan.App | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
# On top (above the taskbar) except while the demo runs, so SketchPad can pop up over it.
function Front([switch]$Normal) {
    $z = if ($Normal) { [IntPtr](-2) } else { [IntPtr](-1) }   # HWND_NOTOPMOST / HWND_TOPMOST
    [Win]::ShowWindow($h, 9) | Out-Null; [Win]::SetWindowPos($h, $z, 0, 0, 1920, 1080, 0x0040) | Out-Null; [Win]::SetForegroundWindow($h) | Out-Null
}
Front
Start-Sleep 2
$rootEl = $A::FromHandle($h)

function Find([string]$name, [int]$timeout = 10) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ($A::NameProperty), $name
    $end = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $end) {
        $el = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    }
    throw "Not found: $name"
}
function FindLike([string]$pattern, [int]$timeout = 10) {
    $end = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $end) {
        foreach ($el in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($el.Current.Name -match $pattern) { return $el }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Nothing matching: $pattern"
}
function Click($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Choose([string]$name) { (Find $name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Enter-Text([string]$name, [string]$text) { (Find $name).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }

$marks = [System.Collections.Generic.List[object]]::new()
$clock = [Diagnostics.Stopwatch]::new()
function Mark([string]$label) { $marks.Add([pscustomobject]@{ label = $label; t = [math]::Round($clock.Elapsed.TotalSeconds, 2) }); Write-Host ("{0,7:N1}s  {1}" -f $clock.Elapsed.TotalSeconds, $label) }

# ---- record ----
$raw = Join-Path $out ($(if ($Tail) { 'raw-tail.mkv' } else { 'raw.mkv' }))
if (Test-Path $raw) { Remove-Item $raw -Force }
$psi = New-Object Diagnostics.ProcessStartInfo $ff
$psi.Arguments = "-hide_banner -loglevel error -f gdigrab -framerate 30 -draw_mouse 0 -offset_x 0 -offset_y 0 -video_size 1920x1080 -i desktop -c:v libx264 -preset ultrafast -crf 16 -pix_fmt yuv420p `"$raw`""
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.CreateNoWindow = $true
$rec = [Diagnostics.Process]::Start($psi)
$clock.Start()
Start-Sleep 1.5
Mark 'start'
try {
    Start-Sleep 5
    if ($Tail) { Start-Sleep 2; Mark 'replay'; Click (Find 'Play the black-box minute'); Start-Sleep 7 }
    if (-not $Tail) {
    Front -Normal
    Mark 'demo'
    $demo = Start-Process $cli -ArgumentList 'demo' -WindowStyle Hidden -PassThru
    $demo.WaitForExit()
    Front
    Mark 'demo-end'
    Start-Sleep 7

    Enter-Text "Describe what's wrong" 'my drawing app keeps freezing and closing'
    Start-Sleep 1.5
    Mark 'ask'
    Click (Find 'Investigate')
    FindLike '^LIKELY CAUSE' 120 | Out-Null
    Mark 'answer'
    Start-Sleep 8

    Enter-Text 'Ask a follow-up question' 'is PhotoSync safe to remove?'
    Start-Sleep 1
    Mark 'followup'
    Click (Find 'Send follow-up question')
    FindLike '^(ANSWERED FROM|CHECKED \d)' 120 | Out-Null
    Mark 'followup-done'
    Start-Sleep 7

    Choose 'Incidents'
    Mark 'incidents'
    Start-Sleep 3
    Click (FindLike '^SketchPad froze, Today')
    Start-Sleep 3
    Mark 'replay'
    Click (Find 'Play the black-box minute')
    Start-Sleep 7
    }
    Mark 'narrate'
    Click (Find 'Tell me what happened')
    FindLike '^(TOLD BY THE LOCAL AI|FROM THE RECORDING)' 150 | Out-Null
    Mark 'narrated'
    Start-Sleep 8

    Choose 'Local AI'
    Mark 'localai'
    Start-Sleep 8

    Click (Find 'Dark theme')
    Mark 'dark'
    Start-Sleep 3
    Choose 'Overview'
    Mark 'overview-dark'
    Start-Sleep 6
    Mark 'end'
}
finally {
    $rec.StandardInput.Write('q'); $rec.StandardInput.Close()
    $rec.WaitForExit(20000) | Out-Null
    Front -Normal
    $marks | ConvertTo-Json | Set-Content (Join-Path $out ($(if ($Tail) { 'marks-tail.json' } else { 'marks.json' }))) -Encoding utf8
    # The Dark button saved a preference; put the user's theme back to following Windows.
    try { Click (Find 'Follow Windows theme' 3) } catch { }
}
Write-Host "Recorded $raw"
