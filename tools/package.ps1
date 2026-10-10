<#
  Builds a portable ResMan folder (and zip) that runs on a PC without .NET or the Windows App SDK installed.

    powershell -ExecutionPolicy Bypass -File tools\package.ps1 [-IncludeModel]

  Output: dist\ResMan\        app\ (all programs), kb\, models\, Start ResMan.cmd, Run demo.cmd, README.txt
          dist\ResMan-win-x64.zip   (without the 2.4 GB model unless -IncludeModel)
#>
param([switch]$IncludeModel)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'ResMan'
$app = Join-Path $out 'app'

# Running copies lock their files.
Get-Process ResMan.App, ResMan.Agent, ResMan.Service, ResMan.Inference -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

$projects = 'src\ResMan.Service', 'src\ResMan.Agent', 'src\ResMan.Inference', 'src\ResMan.Cli', 'src\ResMan.App', 'demo\SketchPad', 'demo\PhotoSync'
foreach ($p in $projects) {
    Write-Host "Publishing $p"
    dotnet publish (Join-Path $root $p) -c Release -r win-x64 --self-contained true -o $app -p:DebugType=none -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $p" }
}

Copy-Item (Join-Path $root 'kb') (Join-Path $out 'kb') -Recurse
New-Item -ItemType Directory -Force (Join-Path $out 'models') | Out-Null
Copy-Item (Join-Path $root 'models\README.md') (Join-Path $out 'models\README.md')
if ($IncludeModel) { Copy-Item (Join-Path $root 'models\*.gguf') (Join-Path $out 'models') }

Set-Content (Join-Path $out 'Start ResMan.cmd') -Encoding ascii -Value @'
@echo off
rem Starts the recorder and the agent (tray icon + local API), then opens the window.
rem Run as administrator for boot-time events and drive wear counters (optional).
cd /d "%~dp0app"
start "ResMan service" /min ResMan.Service.exe
timeout /t 3 /nobreak >nul
start "ResMan agent" /min ResMan.Agent.exe
timeout /t 6 /nobreak >nul
start "" ResMan.App.exe
'@

Set-Content (Join-Path $out 'Run demo.cmd') -Encoding ascii -Value @'
@echo off
rem Stages the demo incident: PhotoSync eats memory, then SketchPad freezes and crashes.
rem Start ResMan first. Keep SketchPad's window in front while it freezes.
cd /d "%~dp0app"
resman.exe demo
pause
'@

Set-Content (Join-Path $out 'Stop ResMan.cmd') -Encoding ascii -Value @'
@echo off
taskkill /im ResMan.App.exe /f >nul 2>&1
taskkill /im ResMan.Agent.exe /f >nul 2>&1
taskkill /im ResMan.Inference.exe /f >nul 2>&1
taskkill /im ResMan.Service.exe /f >nul 2>&1
echo ResMan stopped.
'@

Set-Content (Join-Path $out 'README.txt') -Encoding utf8 -Value @'
ResMan: a black box recorder for Windows, with a local AI that investigates freezes and crashes.
Everything runs on this PC. Nothing is sent online.

1. Model: put Qwen3-4B-Instruct-2507-Q4_K_M.gguf (about 2.4 GB, Apache 2.0) in the "models" folder.
   Download: https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF
   Without a model, ResMan still records and explains incidents with its built-in checks.
2. Double-click "Start ResMan.cmd". A tray icon appears and the ResMan window opens.
3. Optional demo: double-click "Run demo.cmd", then in the window ask
   "my drawing app keeps freezing and closing".
4. "Stop ResMan.cmd" stops everything.

Needs Windows 10 (1809) or later, 64-bit. A GPU with 4 GB+ of memory is recommended (Vulkan); without one the AI runs on the CPU, slower.
Data lives in %ProgramData%\ResMan (recordings) and %LOCALAPPDATA%\ResMan (settings, logs).
'@

$zip = Join-Path $dist 'ResMan-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Host "Zipping"
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Done: {0} ({1:N0} MB unpacked, {2:N0} MB zipped)" -f $out, $size, ((Get-Item $zip).Length / 1MB))
