# Hindsight: a black box recorder for Windows, with a local AI detective

When your PC freezes, crashes or drops off the internet, Windows' own logs mostly say *that* it happened, not *why*.

Hindsight runs quietly in the background and records what your PC was doing:
- the minute before every freeze or crash, at 2-second resolution;
- what changed recently (installs, updates, drivers, startup items);
- your network state, drive health and resource use.

When something goes wrong, a **local AI model investigates that evidence like a detective**. It picks tools, connects signals, and explains the cause in plain language, citing what it found.

## Why local AI (and not a cloud chatbot)

- **The evidence is private.** It includes process lists, window activity, installed software and network names. None of it leaves the PC.
- **It works when the internet doesn't.** "Why did my internet drop?" works with Wi-Fi off, because the evidence and the model are both on the PC.
- **No cost and no account.** The model runs on the GPU you already have: Qwen3-4B (Q4, 2.4 GB) on Vulkan.
- **Fast.** A typical investigation makes 3–5 tool-using model calls and takes about 7–10 s on an RTX 4050 laptop.
- **Light when idle.** The model loads only on demand, then unloads after 30 s so its RAM and VRAM go back to the system. Hindsight itself uses about 70 MB.

## What the AI actually does

The analyzers and signatures are evidence providers. The model is the reasoner:
- **Symptom-first investigation.** It turns a free-form complaint into a real tool-calling loop: at least 2 evidence steps, at most 5, with GBNF-constrained output.
- **Tools:** `get_health_events`, `get_event_details` (the moment of an event plus the black-box minute before it), `get_resource_history` (who grew, who was steady, CPU spikes), `get_changes`, `get_network_history`, `get_findings`, `search_knowledge`, `inspect_binary` (publisher, signature and location of unknown programs), `get_top_processes`.
- **Connecting signals,** for example a freeze, a memory hog that appeared 10 minutes earlier, and a crash with an out-of-memory exception.
- **No-signature cases,** for example periodic stutters from an unknown service, found from resource history alone.
- **Follow-up questions** about a diagnosis ("is it safe to remove?"), reusing the evidence already gathered.
- **Black-box replay narration:** the story of the minute before a freeze.

Engineering behind the speed:
- **Prompt-prefix KV-cache reuse** across tool steps. The static instructions come first and only new evidence is processed (900 of 1,200 tokens reused per step).
- **Micro-batch tuning.** On this RTX 4050 with Vulkan, micro-batches of 256 or more were about 6× slower. Hindsight uses 128 and decodes at about 1,450 prompt tokens/s.
- **Greedy decoding,** so the same evidence always gives the same answer, which keeps the demo reproducible.

## Demo (about 2 minutes)

Run each in its own terminal:

```bash
dotnet run --project src/ResMan.Service
```
```bash
dotnet run --project src/ResMan.Agent
```

Open the window from the tray icon ("Open Hindsight", or double-click it), or run `src/ResMan.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/ResMan.App.exe`. The CLI (`src/ResMan.Cli/bin/Debug/net10.0-windows10.0.19041.0/resman.exe`) stages the incident.

1. **Stage the incident:**
   ```bash
   resman demo
   ```
   PhotoSync (an unknown background app) eats memory, then SketchPad freezes and crashes 3 times. Hindsight preloads the model afterwards. **Overview** turns orange: big crash and freeze counts, and the memory trace climbing past 90% with each incident marked under it.
2. **Ask what's wrong:** on **Ask**, type "my drawing app keeps freezing and closing". Each check the AI runs appears as it happens: what it looked at, what it found, how long it took. The answer then names the cause, the evidence it relied on, and what to do.
3. **Ask a follow-up** in "Keep asking": "is PhotoSync safe to remove?"
4. **Replay the freeze:** on **Incidents**, pick the freeze and press play to watch the black-box minute. Drag across the chart to see the biggest programs at each moment, and press "Tell me what happened" for the AI's narration.
5. **Show the footprint:** **Local AI** shows live RAM and VRAM, speed, load time, and "0 bytes sent online".
6. **Offline:** turn Wi-Fi off and ask "why did my internet drop?"

The theme follows Windows by default; the toggle at the bottom of the menu switches between System, Light and Dark, and the choice is remembered.

For demos the app takes `--page ask|incidents|history|ai|overview`, `--ask "<question>"`, `--event <id>` and `--theme light|dark` (theme not saved).

CLI equivalents: `why`, `followup`, `replay <id> --narrate`, `ai`, plus `status`, `issues`, `timeline`, `changes`, `drives`, `report`, `fixed`, `fixes`, `watch`, `preload`.

## Architecture

The code still uses the working name **ResMan** for its projects, executables and data folders (`%ProgramData%\ResMan`, `%LOCALAPPDATA%\ResMan`); everything a user sees says Hindsight.

| Process | Runs as | Job |
|---|---|---|
| `ResMan.Service` | SYSTEM (console app in development) | Every 2 s: the black-box ring buffer. Every 10 s: snapshots and network samples. Health events, change inventory, drive health. The only database writer. No AI. |
| `ResMan.Agent` | The user | Hang detector (instant freeze notes), tray icon, the Brain (harness and tools), local API on `127.0.0.1` ([docs/api.md](docs/api.md)) |
| `ResMan.Inference` | The user, on demand | LLamaSharp/llama.cpp sidecar on Vulkan, with a persistent context and prefix caching. Exits when idle. |
| `ResMan.App` | The user, while its window is open | WinUI 3 window: Overview, Ask (live investigation), Incidents (black-box replay), History, Local AI. Talks only to the Agent API. |
| `resman` | The user | CLI client and demo script |

## Build, test, evaluate

```bash
dotnet build ResMan.slnx
```
```bash
dotnet test tests/ResMan.Tests
```
```bash
dotnet run --project evals/ResMan.Evals
```

The model: put `Qwen3-4B-Instruct-2507-Q4_K_M.gguf` in `models/` (see [models/README.md](models/README.md)).

**Portable package** (runs on a PC without .NET or the Windows App SDK installed):
```bash
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```
This writes `dist\Hindsight\` and `dist\Hindsight-win-x64.zip` (about 160 MB; the model isn't included, so add `-IncludeModel` to bundle it). The package contains "Start Hindsight.cmd", "Run demo.cmd", "Stop Hindsight.cmd" and a README.

[evals/README.md](evals/README.md) describes the honest A/B/C comparison: a big model with logs only, a big model with Hindsight's evidence, and the local 4B model with the harness. Latest local result: **14/14, median 8.4 s per diagnosis** (2026-10-10). One case, the periodic stutter, failed an earlier run that day by suggesting an antivirus scan, so it can flip between runs.

Run the service elevated to get boot-performance events and drive wear counters.

## Status

**Done:** the backend and the WinUI 3 app for the hackathon build.

**Design:** "Instrument": warm grey or near-black ground, one orange signal color used only for problems, Schibsted Grotesk, IBM Plex Mono and Sofia Sans Extra Condensed (all OFL-licensed and bundled).

**Cut for the hackathon:** installer, signing, optimizer and profiles (they stay as dry-run stubs).

## License

Hindsight is MIT licensed (see [LICENSE](LICENSE)). The Qwen3 model it uses is Apache 2.0 and is downloaded separately. The bundled fonts are under the SIL Open Font License; their licence texts ship next to them in `src/ResMan.App/Assets/Fonts`.
