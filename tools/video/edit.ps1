<#
  Cuts dist\video\raw.mkv into the final demo video using the timestamps in marks.json:
  title card, captioned segments (waiting stretches sped up and labelled), end card. No audio, no music.
  Cards and caption bars are small HTML pages rendered by headless Edge, so they use the app's own fonts and weights.

    powershell -ExecutionPolicy Bypass -File tools\video\edit.ps1

  Output: dist\Hindsight-demo.mp4 (1920x1080, 30 fps, H.264).
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dir = Join-Path $root 'dist\video'

function Find-Tool([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $link = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\$name.exe"
    if (Test-Path $link) { return $link }
    $pkg = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Directory -Filter 'Gyan.FFmpeg*' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter "$name.exe" } | Select-Object -First 1
    if ($pkg) { return $pkg.FullName }
    throw "$name not found. Install it with: winget install Gyan.FFmpeg"
}
$ff = Find-Tool 'ffmpeg'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge is needed to render the cards.' }

$m = @{}; (Get-Content (Join-Path $dir 'marks.json') -Raw | ConvertFrom-Json) | ForEach-Object { $m[$_.label] = [double]$_.t }
foreach ($f in @(@('SchibstedGrotesk.ttf', 'words.ttf'), @('SofiaSansExtraCondensed.ttf', 'numbers.ttf'), @('IBMPlexMono-SemiBold.ttf', 'mono.ttf'))) {
    Copy-Item (Join-Path $root "src\ResMan.App\Assets\Fonts\$($f[0])") (Join-Path $dir $f[1]) -Force
}
Push-Location $dir
Get-ChildItem -Include 'part*.mp4', 'cap*.png', 'cap*.html' -Recurse | Remove-Item -Force

$head = @'
<!doctype html><html><head><meta charset="utf-8"><style>
@font-face{font-family:W;src:url(words.ttf);font-weight:100 900}
@font-face{font-family:N;src:url(numbers.ttf);font-weight:100 900}
@font-face{font-family:M;src:url(mono.ttf)}
html,body{margin:0;width:1920px;height:1080px;overflow:hidden}
'@

function Render([string]$name, [string]$html, [switch]$Transparent) {
    $page = Join-Path $dir "$name.html"; $png = Join-Path $dir "$name.png"
    [IO.File]::WriteAllText($page, $html)
    if (Test-Path $png) { Remove-Item $png -Force }
    $args = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', '--window-size=1920,1080', "`"--screenshot=$png`"")
    if ($Transparent) { $args += '--default-background-color=00000000' }
    $args += "`"file:///$($page.Replace('\', '/'))`""
    # Edge reports "bytes written" on stderr; run it as its own process so that isn't treated as an error.
    Start-Process $edge -ArgumentList $args -Wait -WindowStyle Hidden -RedirectStandardError (Join-Path $dir 'edge.log')
    for ($i = 0; $i -lt 40 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path $png)) { throw "Edge didn't render $name" }
    return "$name.png"
}
function Esc([string]$s) { [Net.WebUtility]::HtmlEncode($s) }

$capN = 0
# A caption bar along the bottom: ink, with an orange tick, in the app's typeface; optional speed tag on the right.
function Caption([string]$text, [string]$tag) {
    $script:capN++
    $tagHtml = if ($tag) { "<div class='tag'>$(Esc $tag)</div>" } else { '' }
    Render "cap$script:capN" ($head + @"
body{background:transparent}
.bar{position:absolute;left:0;right:0;bottom:0;height:120px;background:rgba(20,20,20,.93);display:flex;align-items:center;padding:0 80px;gap:22px}
.tick{width:28px;height:4px;background:#E8590C;flex:none}
.t{font:600 36px/1.2 W;letter-spacing:-.01em;color:#EFEEEA;flex:1}
.tag{font:22px M;letter-spacing:.08em;color:#FF8A57;flex:none}
</style></head><body><div class='bar'><div class='tick'></div><div class='t'>$(Esc $text)</div>$tagHtml</div></body></html>
"@) -Transparent
}

$enc = @('-c:v', 'libx264', '-preset', 'slow', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', '30', '-an')
$parts = [System.Collections.Generic.List[string]]::new()

function Clip([double]$from, [double]$to, [double]$speed, [string]$text, [string]$tag) {
    if ($to - $from -lt 0.3) { return }
    $cap = Caption $text $tag
    $out = "part{0:D2}.mp4" -f $parts.Count
    $len = ($to - $from) / $speed
    $fc = "[0:v]setpts=PTS/$speed[v];[v][1:v]overlay=0:0,fade=t=in:st=0:d=0.25,fade=t=out:st=$([math]::Max(0, $len - 0.25)):d=0.25[o]"
    & $ff -hide_banner -loglevel error -y -ss $from -t ($to - $from) -i raw.mkv -i $cap -filter_complex $fc -map '[o]' @enc $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $out" }
    $parts.Add($out)
}
function Card([string]$png, [double]$seconds) {
    $out = "part{0:D2}.mp4" -f $parts.Count
    & $ff -hide_banner -loglevel error -y -loop 1 -t $seconds -framerate 30 -i $png -vf "format=yuv420p,fade=t=in:st=0:d=0.4,fade=t=out:st=$($seconds - 0.4):d=0.4" @enc $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $out" }
    $parts.Add($out)
}

# ---- title ----
$title = Render 'title' ($head + @'
body{background:#141414}
.wrap{position:absolute;left:160px;top:380px}
.tick{width:64px;height:8px;background:#E8590C;margin-bottom:28px}
h1{margin:0;font:800 156px/1 W;letter-spacing:-0.035em;color:#EFEEEA}
p{margin:34px 0 0;font:400 40px/1.3 W;color:#B9B6AE}
.tag{margin-top:26px;font:24px M;letter-spacing:.08em;color:#FF8A57}
</style></head><body><div class="wrap"><div class="tick"></div><h1>Hindsight</h1>
<p>A black box recorder for your PC, with a local AI that explains what went wrong.</p>
<div class="tag">RUNS ENTIRELY ON THIS PC · NOTHING SENT ONLINE</div></div></body></html>
'@)
Card $title 4.5

# ---- the run ----
Clip $m['start'] $m['demo'] 1 'Hindsight runs quietly in the background, recording what your PC is doing.' ''
Clip $m['demo'] $m['demo-end'] 4 'A staged incident: an unknown app eats memory, then a drawing app freezes and crashes.' '4× SPEED'
Clip $m['demo-end'] $m['ask'] 1 'It caught every freeze and crash, and saved the minute before each one.' ''
Clip $m['ask'] $m['answer'] 1 'Ask in your own words. The local AI picks what to check, step by step, live.' 'REAL TIME'
Clip $m['answer'] $m['followup'] 1 'Its answer names the cause and cites the evidence it relied on.' ''
$fu = $m['followup-done'] - $m['followup']
$fuSpeed = if ($fu -gt 12) { 2 } else { 1 }
Clip $m['followup'] $m['followup-done'] $fuSpeed 'Follow-up questions reuse what it already found.' ($(if ($fuSpeed -gt 1) { '2× SPEED' } else { 'REAL TIME' }))
Clip $m['followup-done'] $m['incidents'] 1 'Follow-up questions reuse what it already found.' ''
Clip $m['incidents'] $m['narrate'] 1 'Black-box replay: the minute before SketchPad froze.' ''
Clip $m['narrate'] $m['narrated'] 2 'The local AI tells the story of that minute.' '2× SPEED'
Clip $m['narrated'] $m['localai'] 1 'The local AI tells the story of that minute.' ''
Clip $m['localai'] $m['dark'] 1 'Every number is measured live: memory, graphics memory, speed. Zero bytes sent online.' ''
Clip $m['dark'] $m['end'] 1 'Light or dark, following Windows or your choice.' ''

# ---- end card ----
$end = Render 'end' ($head + @'
body{background:#141414}
.wrap{position:absolute;left:160px;top:230px;right:160px}
.tick{width:64px;height:8px;background:#E8590C;margin-bottom:26px}
h1{margin:0;font:800 104px/1 W;letter-spacing:-0.035em;color:#EFEEEA}
.stats{display:flex;gap:120px;margin-top:90px}
.n{font:900 168px/.85 N;letter-spacing:-.01em}
.l{margin-top:16px;font:500 30px W;color:#B9B6AE}
.foot{margin-top:110px;font:22px M;letter-spacing:.04em;color:#9C9A93}
</style></head><body><div class="wrap"><div class="tick"></div><h1>Hindsight</h1>
<div class="stats">
 <div><div class="n" style="color:#FF8A4C">14/14</div><div class="l">test cases diagnosed</div></div>
 <div><div class="n" style="color:#FF8A4C">8.4 s</div><div class="l">per diagnosis, median</div></div>
 <div><div class="n" style="color:#5FD49A">0</div><div class="l">bytes sent online</div></div>
</div>
<div class="foot">QWEN3-4B (OPEN-WEIGHT, APACHE 2.0) ON LLAMA.CPP · .NET 10 + WINUI 3 · MIT LICENSED</div></div></body></html>
'@)
Card $end 6

# ---- join ----
$parts | ForEach-Object { "file '$_'" } | Set-Content list.txt -Encoding ascii
$final = Join-Path $root 'dist\Hindsight-demo.mp4'
& $ff -hide_banner -loglevel error -y -f concat -safe 0 -i list.txt -c copy -movflags +faststart $final
if ($LASTEXITCODE -ne 0) { throw 'concat failed' }
Pop-Location
$d = & (Find-Tool 'ffprobe') -v error -show_entries format=duration -of csv=p=0 $final
Write-Host ("Done: {0} ({1:N0} s, {2:N1} MB)" -f $final, [double]$d, ((Get-Item $final).Length / 1MB))
