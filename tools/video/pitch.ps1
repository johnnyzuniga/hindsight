<#
  Builds the Hindsight pitch video from dist\video\raw.mkv (recorded by record.ps1) plus animated text scenes.
  No voice, no music: everything on screen is the app's own fonts and colors, or the real app.

    powershell -ExecutionPolicy Bypass -File tools\video\pitch.ps1 [-Repo "github.com/you/hindsight"]

  Output: dist\Hindsight-pitch.mp4 (1920x1080, 30 fps, H.264).
#>
param([string]$Repo = '')
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

$m = @{}; (Get-Content (Join-Path $dir 'marks.json') -Raw | ConvertFrom-Json) | ForEach-Object { $m[$_.label] = [double]$_.t }
foreach ($f in @(@('SchibstedGrotesk.ttf', 'words.ttf'), @('SofiaSansExtraCondensed.ttf', 'numbers.ttf'), @('IBMPlexMono-SemiBold.ttf', 'mono.ttf'))) {
    Copy-Item (Join-Path $root "src\ResMan.App\Assets\Fonts\$($f[0])") (Join-Path $dir $f[1]) -Force
}
Push-Location $dir
# Replay, narration and Local AI can come from a separate "tail" recording (record.ps1 -Tail) that patches the main run.
$tm = $m; $ts = 'raw.mkv'
if (Test-Path (Join-Path $dir 'marks-tail.json')) {
    $tm = @{}; (Get-Content (Join-Path $dir 'marks-tail.json') -Raw | ConvertFrom-Json) | ForEach-Object { $tm[$_.label] = [double]$_.t }
    $ts = 'raw-tail.mkv'
}
Get-ChildItem -File | Where-Object { $_.Name -match '^(p\d+|L_|cap)' } | Remove-Item -Force

# ---------- rendering helpers ----------
$css = @'
<!doctype html><html><head><meta charset="utf-8"><style>
@font-face{font-family:W;src:url(words.ttf);font-weight:100 900}
@font-face{font-family:N;src:url(numbers.ttf);font-weight:100 900}
@font-face{font-family:M;src:url(mono.ttf)}
html,body{margin:0;width:1920px;height:1080px;overflow:hidden;background:transparent;color:#EFEEEA;font-family:W}
.x{position:absolute;left:160px;right:160px}
.label{font:22px M;letter-spacing:.1em;color:#FF8A57}
.tick{width:64px;height:8px;background:#E8590C}
.o{color:#FF8A4C}
'@
function Render([string]$name, [string]$style, [string]$body) {
    $page = Join-Path $dir "$name.html"; $png = Join-Path $dir "$name.png"
    [IO.File]::WriteAllText($page, "$css$style</style></head><body>$body</body></html>")
    if (Test-Path $png) { Remove-Item $png -Force }
    $a = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', '--window-size=1920,1080',
        '--default-background-color=00000000', "`"--screenshot=$png`"", "`"file:///$($page.Replace('\', '/'))`"")
    Start-Process $edge -ArgumentList $a -Wait -WindowStyle Hidden -RedirectStandardError (Join-Path $dir 'edge.log')
    for ($i = 0; $i -lt 40 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path $png)) { throw "Edge didn't render $name" }
    return "$name.png"
}
$L = 0
function Layer([string]$style, [string]$body) { $script:L++; Render "L_$script:L" $style $body }

$enc = @('-c:v', 'libx264', '-preset', 'slow', '-crf', '19', '-pix_fmt', 'yuv420p', '-r', '30', '-an')
$parts = [System.Collections.Generic.List[string]]::new()
function Next { "p{0:D2}.mp4" -f $parts.Count }

# A text scene: ink ground, layers fading in and rising 24px at their own times (the staggered reveal).
function Scene([double]$seconds, [object[]]$layers, [string]$ground = '0x141414') {
    $out = Next
    $in = @('-f', 'lavfi', '-i', "color=c=${ground}:s=1920x1080:r=30:d=$seconds")
    $fc = ''; $last = '0:v'; $k = 0
    foreach ($ly in $layers) {
        $k++; $in += @('-loop', '1', '-t', $seconds, '-framerate', '30', '-i', $ly.png)
        $at = $ly.at
        $fc += "[${k}:v]format=rgba,fade=t=in:st=${at}:d=0.5:alpha=1[a$k];"
        $fc += "[$last][a$k]overlay=x=0:y='if(lt(t,$at),24,max(0,24-24*(t-$at)/0.5))':eval=frame[s$k];"
        $last = "s$k"
    }
    $fc += "[$last]fade=t=in:st=0:d=0.3,fade=t=out:st=$($seconds - 0.4):d=0.4[o]"
    & $ff -hide_banner -loglevel error -y @in -filter_complex $fc -map '[o]' -t $seconds @enc $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on scene $out" }
    $parts.Add($out)
}

# Caption bar along the bottom of footage; optional tag on the right (REAL TIME, 4x SPEED).
$capN = 0
function Caption([string]$text, [string]$tag) {
    $script:capN++
    $t = if ($tag) { "<div class='tag'>$([Net.WebUtility]::HtmlEncode($tag))</div>" } else { '' }
    Render "cap$script:capN" @'
.bar{position:absolute;left:0;right:0;bottom:0;height:120px;background:rgba(20,20,20,.93);display:flex;align-items:center;padding:0 80px;gap:22px}
.bar .t{font:600 36px/1.2 W;letter-spacing:-.01em;flex:1}
.tag{font:22px M;letter-spacing:.08em;color:#FF8A57;flex:none}
.bar .tick{width:28px;height:4px;flex:none}
'@ "<div class='bar'><div class='tick'></div><div class='t'>$([Net.WebUtility]::HtmlEncode($text))</div>$t</div>"
}

# A piece of the recording: optional speed-up, optional zoom (crop x,y,w,h of the 1920x1080 frame), caption.
function Footage([double]$from, [double]$to, [double]$speed, [string]$text, [string]$tag, [int[]]$zoom, [string]$src = 'raw.mkv') {
    if ($to - $from -lt 0.3) { return }
    $cap = Caption $text $tag
    $out = Next
    $len = ($to - $from) / $speed
    $z = if ($zoom) { "crop=$($zoom[2]):$($zoom[3]):$($zoom[0]):$($zoom[1]),scale=1920:1080:flags=lanczos," } else { '' }
    $fc = "[0:v]setpts=PTS/$speed,${z}fps=30[v];[v][1:v]overlay=0:0,fade=t=in:st=0:d=0.3,fade=t=out:st=$([math]::Max(0, $len - 0.3)):d=0.3[o]"
    & $ff -hide_banner -loglevel error -y -ss $from -t ($to - $from) -i $src -i $cap -filter_complex $fc -map '[o]' @enc $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on footage $out" }
    $parts.Add($out)
}

# ---------- 1. Hook ----------
Scene 6 @(
    @{ png = (Layer '.a{top:400px;font:800 120px/1 W;letter-spacing:-.035em}' '<div class="x a">Your PC froze. Again.</div>'); at = 0.3 },
    @{ png = (Layer '.b{top:560px;font:400 44px W;color:#B9B6AE}' '<div class="x b">You restart. Tomorrow it happens again.</div>'); at = 2.2 })

# ---------- 2. The problem: the real entry Windows wrote for this crash ----------
$event = @(
    'Faulting application name: SketchPad.exe, version: 1.0.0.0',
    'Faulting module name: KERNELBASE.dll, version: 10.0.26100.9549',
    'Exception code: 0xe0434352',
    'Fault offset: 0x00000000000c483a') | ForEach-Object { [Net.WebUtility]::HtmlEncode($_) }
Scene 9.5 @(
    @{ png = (Layer '.l{top:230px}' '<div class="x label l">WHAT WINDOWS RECORDS WHEN AN APP CRASHES</div>'); at = 0.2 },
    @{ png = (Layer '.box{top:290px;background:#1E1D1A;border:1px solid #2E2D2A;border-left:4px solid #E8590C;padding:34px 40px;font:26px/1.7 M;color:#C9C6BE}' "<div class='x box'>$($event -join '<br>')</div>"); at = 0.6 },
    @{ png = (Layer '.c{top:690px;font:700 64px/1.1 W;letter-spacing:-.03em}' '<div class="x c">Windows records <span class="o">that</span> it happened.<br>Never <span class="o">why</span>.</div>'); at = 4.2 })

# ---------- 3. Who it is for ----------
Scene 5.5 @(
    @{ png = (Layer '.a{top:420px;font:700 68px/1.15 W;letter-spacing:-.03em}' '<div class="x a">For everyone who isn''t the family IT person,</div>'); at = 0.3 },
    @{ png = (Layer '.b{top:520px;font:700 68px/1.15 W;letter-spacing:-.03em}' '<div class="x b o">and for the one who is.</div>'); at = 2.0 })

# ---------- 4. The idea ----------
$trace = '<svg width="1600" height="200" viewBox="0 0 1600 200"><line x1="0" y1="40" x2="1600" y2="40" stroke="#E8590C" stroke-width="2" stroke-dasharray="6 8" opacity=".6"/>' +
         '<polyline points="0,150 300,148 520,152 700,146 800,120 900,90 1000,66 1100,50 1200,40 1320,36 1600,34" fill="none" stroke="#FF8A4C" stroke-width="6" stroke-linejoin="round" stroke-linecap="round"/>' +
         '<rect x="1318" y="0" width="10" height="200" fill="#E8590C"/></svg>'
Scene 7 @(
    @{ png = (Layer '.t{top:300px}' '<div class="x"><div class="tick t" style="position:absolute;top:300px"></div></div><div class="x" style="top:330px;font:800 156px/1 W;letter-spacing:-.035em">Hindsight</div>'); at = 0.3 },
    @{ png = (Layer '.b{top:510px;font:400 46px W;color:#B9B6AE}' '<div class="x b">A black box recorder for your PC.</div>'); at = 1.3 },
    @{ png = (Layer '.s{top:640px}' "<div class='x s'>$trace</div>"); at = 2.4 })

# ---------- 5. How it works ----------
$col = '.cols{top:420px;display:flex;gap:80px}.c{flex:1}.n{font:900 120px/.9 N;color:#FF8A4C}.h{margin-top:22px;font:700 40px/1.15 W;letter-spacing:-.02em}.d{margin-top:12px;font:400 26px/1.35 W;color:#9C9A93}'
function Col([int]$i, [string]$n, [string]$h, [string]$d) {
    $pad = '<div class="c"></div>' * ($i)
    $rest = '<div class="c"></div>' * (2 - $i)
    Layer $col "<div class='x cols'>$pad<div class='c'><div class='n'>$n</div><div class='h'>$h</div><div class='d'>$d</div></div>$rest</div>"
}
Scene 9.5 @(
    @{ png = (Layer '.l{top:340px}' '<div class="x label l">HOW IT WORKS</div>'); at = 0.2 },
    @{ png = (Col 0 '01' 'Records quietly' 'Always on, about 70 MB. No cloud, no account.'); at = 0.7 },
    @{ png = (Col 1 '02' 'Keeps the minute before every crash' 'A 2-second black box, saved around each freeze and crash.'); at = 2.0 },
    @{ png = (Col 2 '03' 'A local AI investigates' 'It picks what to check, then explains the cause in plain words.'); at = 3.3 })

# ---------- 6. The demo (the real app) ----------
$fullFrame = $null
# Starts 4.5 s in: the first seconds of the take caught other windows on screen.
Footage ($m['demo'] + 4.5) $m['demo-end'] 6 'Live demo: an unknown app eats memory, then a drawing app freezes and crashes.' '6× SPEED' $fullFrame
Footage $m['demo-end'] ([math]::Min($m['ask'], $m['demo-end'] + 4)) 1 'Hindsight caught every one, and kept the minute before each.' '' $fullFrame
Footage $m['ask'] $m['answer'] 1 'Ask in plain words. The local AI decides what to check, live.' 'REAL TIME' @(200, 0, 1720, 968)
Footage $m['answer'] ([math]::Min($m['followup'], $m['answer'] + 6)) 1 'It names the cause and shows the evidence it relied on.' '' @(200, 0, 1720, 968)
Footage $m['followup'] $m['followup-done'] 2 'Follow-up questions reuse what it already found.' '2× SPEED' $fullFrame
Footage $tm['replay'] $tm['narrate'] 1 'Black-box replay: the minute before the freeze.' '' $fullFrame $ts
Footage $tm['narrate'] $tm['narrated'] 1 '…and the local AI tells the story of that minute.' 'REAL TIME' $fullFrame $ts
Footage $tm['narrated'] ([math]::Min($tm['localai'], $tm['narrated'] + 6)) 1 '…and the local AI tells the story of that minute.' '' $fullFrame $ts

# ---------- 7. Why local AI ----------
$row = '.r{display:flex;gap:28px;align-items:baseline}.k{width:220px;font:700 44px W;color:#FF8A4C;letter-spacing:-.02em;flex:none}.v{font:400 40px W;color:#EFEEEA}'
Scene 10 @(
    @{ png = (Layer '.l{top:300px}' '<div class="x label l">WHY LOCAL AI</div>'); at = 0.2 },
    @{ png = (Layer "$row .p{top:370px}" '<div class="x r p"><div class="k">Private</div><div class="v">Your PC''s history never leaves your PC.</div></div>'); at = 0.6 },
    @{ png = (Layer "$row .p{top:470px}" '<div class="x r p"><div class="k">Offline</div><div class="v">Works with no internet at all.</div></div>'); at = 1.6 },
    @{ png = (Layer "$row .p{top:570px}" '<div class="x r p"><div class="k">Free</div><div class="v">No account, no subscription. Open source.</div></div>'); at = 2.6 },
    @{ png = (Layer "$row .p{top:670px}" '<div class="x r p"><div class="k">Light</div><div class="v">About 70 MB until you ask; the AI unloads itself after.</div></div>'); at = 3.6 })
Footage $tm['localai'] ([math]::Min($tm['dark'], $tm['localai'] + 5)) 1 'Every number measured live, on this PC: zero bytes sent online.' '' $fullFrame $ts

# ---------- 8. Proof ----------
$stat = '.stats{top:400px;display:flex;gap:140px}.n{font:900 176px/.85 N}.d{margin-top:18px;font:500 30px W;color:#B9B6AE}'
function Stat([int]$i, [string]$n, [string]$color, [string]$d) {
    $pad = '<div style="width:0"></div>'
    $cells = @('', '', '')
    $cells[$i] = "<div><div class='n' style='color:$color'>$n</div><div class='d'>$d</div></div>"
    for ($j = 0; $j -lt 3; $j++) { if (-not $cells[$j]) { $cells[$j] = "<div style='visibility:hidden'><div class='n'>$(@('14/14','8.4 s','0')[$j])</div><div class='d'>.</div></div>" } }
    Layer $stat "<div class='x stats'>$($cells -join '')</div>"
}
Scene 7 @(
    @{ png = (Layer '.l{top:320px}' '<div class="x label l">TESTED ON 14 STAGED PROBLEMS, ON A LAPTOP GPU</div>'); at = 0.2 },
    @{ png = (Stat 0 '14/14' '#FF8A4C' 'correctly diagnosed'); at = 0.6 },
    @{ png = (Stat 1 '8.4 s' '#FF8A4C' 'per diagnosis, median'); at = 1.4 },
    @{ png = (Stat 2 '0' '#5FD49A' 'bytes sent online'); at = 2.2 })

# ---------- 9. Close ----------
$where = if ($Repo) { [Net.WebUtility]::HtmlEncode($Repo) } else { 'Open source on GitHub' }
Scene 6 @(
    @{ png = (Layer '' '<div class="x"><div class="tick" style="position:absolute;top:330px"></div></div><div class="x" style="top:360px;font:800 140px/1 W;letter-spacing:-.035em">Hindsight</div>'); at = 0.2 },
    @{ png = (Layer '.b{top:530px;font:400 44px W;color:#B9B6AE}' '<div class="x b">Know why your PC froze. Without sending it anywhere.</div>'); at = 1.0 },
    @{ png = (Layer '.f{top:660px;font:24px M;letter-spacing:.06em;color:#9C9A93}' "<div class='x f'>$where · MIT LICENSED · QWEN3-4B (OPEN-WEIGHT) ON LLAMA.CPP · .NET 10 + WINUI 3</div>"); at = 1.8 })

# ---------- join ----------
$parts | ForEach-Object { "file '$_'" } | Set-Content list.txt -Encoding ascii
$final = Join-Path $root 'dist\Hindsight-pitch.mp4'
& $ff -hide_banner -loglevel error -y -f concat -safe 0 -i list.txt -c copy -movflags +faststart $final
if ($LASTEXITCODE -ne 0) { throw 'concat failed' }
Pop-Location
$d = & (Find-Tool 'ffprobe') -v error -show_entries format=duration -of csv=p=0 $final
Write-Host ("Done: {0} ({1:N0} s, {2:N1} MB)" -f $final, [double]$d, ((Get-Item $final).Length / 1MB))
