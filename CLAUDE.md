# ResMan

A "black box recorder" for Windows freezes and crashes, with a local AI that investigates them. Built for a Local AI hackathon. The judging rubric weights local-AI implementation and problem/usefulness at 25% each, so the model must do the reasoning, with code supplying the evidence.

## Layout
- `src/ResMan.Service`: recorder (telemetry, black-box ring buffer, event logs, changes, drive health, network). The only DB writer.
- `src/ResMan.Agent`: local API on 127.0.0.1 (see `docs/api.md`), tray icon, hang detector, hosts the Brain.
- `src/ResMan.Brain`: analyzers, signatures, and the agentic harness (`Harness/`: tools, prompts, GBNF grammars).
- `src/ResMan.Inference`: LLamaSharp sidecar (Qwen3-4B Q4, Vulkan). It starts on demand and exits when idle.
- `src/ResMan.App`: WinUI 3 window (unpackaged).
- `src/ResMan.Cli`: `resman` CLI. `resman demo` stages the incident.
- `tests/`, `evals/` (A/B/C eval; cloud models are never wired in, A/B are exported prompts).

## Build, test, run
```
dotnet build ResMan.slnx
dotnet test tests/ResMan.Tests
dotnet run --project src/ResMan.Service
dotnet run --project src/ResMan.Agent
```
- **Stop the processes before building.** ResMan.Service, ResMan.Agent, ResMan.Inference and ResMan.App lock their DLLs; so do any `dotnet run` hosts.
- **The model** goes in `models/` (ignored by git); see `models/README.md`.
- **App start-up arguments:** `--page`, `--ask "<question>"`, `--event <id>`, `--theme light|dark` (the theme isn't saved).

## Gotchas
- **Keep the micro-batch at 128.** 256 and above was about 6× slower on this RTX 4050 with Vulkan.
- **Big games page the model's VRAM out.** Generation then drops to about 4 tok/s; a reboot fixes it, and the Local AI page warns about it. Don't benchmark right after the user has gamed.
- **Exclude ResMan's own processes from evidence everywhere** (`RuleValidator.IsOwnProcess`).
- **Tool outputs are capped at 1,400 chars.** Put conclusions first and detail after.
- **Windows logs a .NET crash twice** (1026 and 1000). `HealthStore.FindSameCrash` merges them.
- **Some files are CRLF.** Use exact-match edits, not regex tools that assume LF.
- **Never interrupt the user's games.** Check for a running game before heavy builds or inference.

## Design
- **The UI follows the user's "anti AI slop" brief**, in auto-memory as `resman-frontend-aesthetics`.
- **The approved direction is "Instrument":**
  - warm grey or near-black ground, ink type;
  - one signal orange used only for problems;
  - in light mode a state-colored band floods behind the hero; in dark mode it's a tinted dark band with a colored accent.
- **Fonts:** Schibsted Grotesk for words, IBM Plex Mono for data, Sofia Sans Extra Condensed for numerals.
- **No costume props** (stamps, folder tabs, glows or grids).
- **Mockups:** the Claude Design canvas, row D: https://claude.ai/artifact/7artCsUxErx4tw38LE5ph6

## Working rules
- **Everything stays free.** No paid services, and no cloud LLM integration.
- **Results stay honest.** Report eval failures and unrepresentative timings as they are.
