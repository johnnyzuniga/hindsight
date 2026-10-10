# Hindsight Agent API (v1)

The Agent runs as the signed-in user and serves a local API that any UI (WinUI, React Native, Flutter, or the `resman` CLI) uses.

## Connecting

- **Address and token:** read `%LOCALAPPDATA%\ResMan\agent.json` → `{ "url": "http://127.0.0.1:<port>", "token": "<hex>", "pid": 1234 }`.
  - Only the signed-in user can read this file.
  - The port is random on every start.
  - If `pid` isn't running, the agent isn't either.
- **Auth:** send `Authorization: Bearer <token>`. WebSockets can pass `?token=<token>` instead.
- **Host:** must be `127.0.0.1` or `localhost`. Anything else gets `400` (protection against DNS rebinding).
- **Format:** JSON uses camelCase, and enums are strings.

Errors come back as `{ "error": "plain-language message" }`:

| Status | Meaning |
|---|---|
| `503` | The service isn't running or has no data yet |
| `409` | The AI is paused while gaming; retry with `?force=true` |
| `404` | Unknown id |

All paths below are under `/api/v1`.

## Status and doctor

| Method | Path | Returns |
|---|---|---|
| GET | `/status` | `{ health, service, agent, ai, footprint }`. `health.level` is `good` / `attention` / `problem`, plus `headline`, `detail`, `hiddenWarnings`. `footprint.totalMb` is private memory. |
| GET | `/issues` | `Issue[]`: `id`, `title`, `explanation`, `evidence?`, `severity` (`info` / `notice` / `warning` / `critical`), `fixes[] { label, uri? }`, `needsHuman`, `eventIds[]`, `lastSeenUtc`. Sorted by severity. |
| GET | `/timeline?days=7` | `TimelineEntry[]`: `eventIds[]`, `timeUtc`, `firstUtc`, `kind`, `title`, `detail?`, `severity`, `signatureId?`, `count`. Repeats of the same thing are collapsed into one entry. |
| GET | `/history?minutes=60` | `{ fromUtc, toUtc, samples[] { timeUtc, cpu, memory, diskMbps }, incidents[] { id, timeUtc, kind, app?, title } }`: resource history (10 s samples) and each problem event in the window. Feeds the app's traces. |
| GET | `/events/{id}` | `{ title, detail, event, snapshot? }`, where `snapshot` is the resource snapshot from the moment of the event. |
| GET | `/findings` | `Finding[]` from the analyzers: `code`, `severity`, `title`, `evidence`, `subject?`, `eventIds[]`, `relatedChange?`. |
| GET | `/changes?days=14` | `{ routineHidden, groups[] { timeUtc, kind, description, count, items[] } }`. Bursts are grouped, and routine antivirus updates are hidden. |
| GET | `/drives` | `DriveHealth[]`: `model`, `mediaType`, `healthStatus`, `wearPercent?`, `readErrorsTotal?`, `writeErrorsTotal?`, `temperatureC?`, `powerOnHours?`. |
| GET | `/report?days=30` | `text/html`: the self-contained helper report. Print it to PDF in a browser. |
| POST | `/scan` | Runs the change inventory and drive health check now. |

## Asking (local model)

| Method | Path | Body | Returns |
|---|---|---|---|
| POST | `/why` | `{ "symptom": "my pc froze" }` | `DoctorAnswer`: `summary`, `likelyCause`, `confidence` (`low` / `medium` / `high`), `steps[]`, `needsHuman`, `actions[] { label, uri? }`, `toolTrace[]`, `usedModel`, `modelMilliseconds`, `note?` |
| POST | `/events/{id}/explain` | none | `DoctorAnswer` for one event. Its context includes the snapshot and the changes in the 3 days before. |

**Watching the investigation live:** add `?requestId=<any id>` to `/why`, `/followup` or `/events/{id}/explain`. While the request runs, each step is sent over `/live` as an `investigation_step` message tagged with that `requestId`. The HTTP response is unchanged.

Without a model (no-AI tier), both endpoints still answer from signatures and analyzers, with `usedModel: false`.
If no symptom-related events are on record, the answer says so plainly instead of guessing.

`DoctorAnswer` also includes:
- `evidence[]`: the facts the model cited
- `sessionId`: for follow-up questions
- `modelCalls`: how many model calls the investigation made

| Method | Path | Body | Returns |
|---|---|---|---|
| POST | `/followup` | `{ "sessionId": "...", "question": "is it safe to remove?" }` | `DoctorAnswer`, reusing the evidence already gathered |
| GET | `/events/{id}/replay` | none | `{ title, incidentUtc, available, samples[] { tSeconds, cpu, memory, diskMbps, top[] } }`: the black-box minute before the event, sampled every 2 s |
| POST | `/events/{id}/replay/narrate` | none | `{ narration, keyMoments[] { t, what }, likelyCause, usedModel, modelMilliseconds }` |
| GET | `/network?hours=2` | none | `NetworkSample[]` (connectivity, SSID, signal bars, router ping), recorded with no internet traffic |

## Local AI footprint

| Method | Path | Body | Returns |
|---|---|---|---|
| GET | `/ai/status` | none | See the fields below. Every number is measured. |
| POST | `/ai/preload` | `{ "minutes": 30 }` | Loads the model now and keeps it warm (for demos): `{ ready, tookMs, backend }` |

`/ai/status` fields:
- `state`: `unloaded` / `loading` / `ready` / `busy`
- `backend`: e.g. "gpu: NVIDIA GeForce RTX 4050 Laptop GPU"
- `sidecar { ramMb, privateMb, vramMb }`
- `timings { loadMs, coldStartMs, lastInferenceMs, tokensPerSecond, lastQuestionMs }`
- `resman { agentMb, serviceMb, totalWithoutAiMb, totalNowMb }`
- `networkUsedByAi`

## Fix tracking

| Method | Path | Body | Returns |
|---|---|---|---|
| POST | `/fixes` | `{ "issueId": "<Issue.id>", "label": "what you did" }` | `{ id, tracking }` |
| GET | `/fixes` | none | `FixOutcome[]`: `fix`, `beforePerWeek`, `afterPerWeek`, `daysSince`, `verdict` (`too_early` / `fixed` / `better` / `no_change` / `worse`), `summary` |

## Live updates (WebSocket)

`GET /live?token=...` sends JSON messages of the form `{ type, data, timeUtc }`:

| type | data |
|---|---|
| `event` | `{ id, timeUtc, kind, title, detail }`: a new health event was recorded |
| `hang_started` | `{ app, pid }`: the foreground app stopped responding |
| `hang` | `{ appName, pid, startUtc, durationSeconds, recovered }`: the freeze ended (or is still going after 30 s) |
| `freeze_explained` | `{ eventId, app, seconds, headline, explanation, causes[], recentChange? }`: the instant explanation, also shown as a tray notification |
| `profile` | `{ profile, foregroundApp, fullscreen, idleSeconds, onBattery }` |
| `investigation_step` | `{ requestId, step, kind, tool?, input?, label?, finding?, ms, elapsedMs }`. `kind` is `thinking` (a model call started), `tool` (evidence gathered; `label` is what was checked in plain words, `finding` its most telling line), `repeat` (a duplicate call skipped) or `answer` (`finding` is the likely cause). |

## Agent control

| Method | Path | Body |
|---|---|---|
| POST | `/agent/pause` | `{ "paused": true }`: pauses hang and context detection |
| GET | `/processes/{name}` | Knowledge-base entry plus what Windows knows about the running binary (publisher, signature, location) |

## Stubs (not part of v1)

`POST /rules/suggest`, `GET/POST /rules`, `DELETE /rules/{id}` and `GET /rules/dry-run` drive the optimizer.
The optimizer is dry-run only and deprioritized.
