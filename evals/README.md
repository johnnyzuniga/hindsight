# A/B/C evaluation

The question: does a small **local** model with Hindsight's captured evidence diagnose PC problems as well as a big cloud model?

| Setup | Model | Gets | How it runs |
|---|---|---|---|
| A | Big cloud model | Event Viewer-style logs only (what you could paste into a chatbot) | Exported prompt: `out/A/<case>.txt` |
| B | Big cloud model | The same evidence Hindsight's tools produce (resource history, the moment of each event, the minute before, changes, network), gathered up front | Exported prompt: `out/B/<case>.txt` |
| C | Local Qwen3-4B (Q4, ~2.4 GB) | The same evidence, through Hindsight's tool-calling harness (it picks the tools) | Runs here on the local GPU |

No cloud model is called by this code. To fill in A and B:
1. Paste each prompt from `out/A` or `out/B` into the model you're comparing against.
2. Save its JSON reply as `in/A/<case>.json` or `in/B/<case>.json`.
3. Run with `--score`.

All three setups use the same deterministic scorer:
- root-cause keyword groups must all be present;
- forbidden claims must be absent;
- `needs_human` must match;
- the case may also require low confidence.

```bash
dotnet run --project evals/ResMan.Evals
```
```bash
dotnet run --project evals/ResMan.Evals -- --export
```
```bash
dotnet run --project evals/ResMan.Evals -- --score
```
```bash
dotnet run --project evals/ResMan.Evals -- --only demo-memory-hog
```

## Cases (`cases.json`)

There are 14 staged cases:
- multi-signal problems
- **no-signature cases** (the periodic stutter: no events at all, only resource history)
- unknown processes
- **offline network diagnosis**, in two variants: a weak signal versus an ISP outage, which look identical in Windows' logs
- drivers, overheating, a failing drive, app updates, disk hogs, slow startup
- honesty checks: nothing relevant on record, or only harmless noise

## Latest results

See [results.md](results.md).

| Run | Setup C result | Median time | Notes |
|---|---|---|---|
| 2026-10-09 | 13/14 | 7.5 s | Healthy GPU. Failure: `nothing-relevant` (below). |
| 2026-10-10, run 1 | 12/14 | ~70 s | After the resource-history fix. `nothing-relevant` now passes; `no-signature-periodic-stutter` failed only for suggesting an antivirus scan (the diagnosis was right); `failing-drive` missed `needs_human`. |
| 2026-10-10, run 2 | 14/14 | ~70 s | After the rule that a hardware fault the AI inspected always sets `needs_human`. |
| 2026-10-10, run 3 | **14/14** | **8.4 s** (max 18.5 s, the first case, which includes loading the model) | Same code as run 2, after a reboot: GPU healthy again. This is the representative result. |

The times of 2026-10-10 runs 1 and 2 are **not representative**: Windows had paged the model's graphics memory out after two games ran (only ~0.6 of ~3.6 GB stayed on the GPU), so generation ran at ~4.5 tokens/s instead of ~48. Re-measure after a reboot.

Answers are greedy (deterministic for the same input), but fixture timestamps differ between runs, so a borderline case can flip; the stutter case did.

The 2026-10-09 failure was `nothing-relevant`: the 4B model linked a two-day-old Wi-Fi drop to a printer complaint with "high" confidence, an overreach. It passed both 2026-10-10 runs; it stays in the set as an honesty check.

Setup A is expected to fail by construction on cases whose evidence never reaches Windows' logs: resource history, the network monitor, drive health counters. That gap is the point of capturing evidence locally.
