# Session Stories — API & Invocation Contract

**This file is the single authority** for routes, ports, folder paths, and the `claude -p`
invocation shape. If code or plan disagrees with this page, this page wins; change it here first.

## Ports & paths

- API + SPA: `http://0.0.0.0:5211` (Kestrel, LAN; firewall Private profile; no auth v1 — all routes under `/api` = future auth seam).
- Vite dev proxy: `/api → http://localhost:5211`.
- `LibraryRoot` default: `<repo>/library` (contains `stories/`, `universes/`, `voice-cache/`). Overridable in `appsettings.json`.
- Models: `<repo>/models` (`chatterbox/`, `whisper/`). Downloaded by `scripts/download-models.ps1`, never committed.
- Skill: `<repo>/skills/story/` (committed content).

## Conventions

- Every editable-file GET returns `ETag` (derived from `LastWriteTimeUtc` + length). Every PUT requires `If-Match`; mismatch → **409 + current content + current ETag**. No file locks anywhere.
- Errors: RFC 7807 `ProblemDetails`. Job failures embed exit code + last 50 stderr lines.
- All long-running work goes through **jobs** — there are no bespoke action routes.
- Scene ids are stable strings `s1, s2…`, never renumbered. Beat ids `b1, b2…` likewise.
- Variants: `session.<variant>.json`; the variant name comes from the filename only. No bare `session.json`.

## Routes

### Universes
| Route | Notes |
|---|---|
| `GET /api/universes` | list ids + titles |
| `GET/PUT /api/universes/{uid}/files/{name}` | `constraints.md`, `bible.md`, `characters.md`, `voices.json`, `tones/{tone}.md` (name may contain one `/` for tones) |
| `GET /api/universes/{uid}/pending-facts` | aggregated `bible.pending.*.md` across that universe's stories → `[{storyId, variant, lineId, text, sceneId}]` |

### Stories
| Route | Notes |
|---|---|
| `GET /api/stories` | list: id, title, universe, variants with pipeline stage |
| `POST /api/stories` | `{slug, universe, variant, title}` → creates folder + `session.<variant>.json` skeleton |
| `GET /api/stories/{sid}` | detail incl. variants, recollections, artifact presence |
| `GET/PUT /api/stories/{sid}/session/{variant}` | raw JSON body; unknown fields preserved server-side (`JsonNode` round-trip) |
| `POST /api/stories/{sid}/recollections?person=x` | multipart upload; **audio files auto-enqueue a `transcribe` job**; recordings are immutable after capture |
| `GET /api/stories/{sid}/recollections` / `…/{file}` | list / fetch (range-enabled for audio playback) |
| `PUT /api/stories/{sid}/recollections/{file}` | **transcripts only** (`.txt`/`.md`): If-Match guarded. Recordings stay immutable. The no-cleaning rule binds the model, not the human — the UI gates this behind an explicit confirm |
| `GET/DELETE /api/stories/{sid}/prev/{variant}/{sceneId}` | the `draft.<v>.<sceneId>.prev.md` one-level undo: GET returns its text (404 when absent), DELETE discards it |
| `GET /api/stories/{sid}/draft/{variant}` | parsed scene DTOs `[{sceneId, title, beats, text, verifyFlags[]}]` — the client never parses draft.md |
| `PUT /api/stories/{sid}/draft/{variant}/scenes/{sceneId}` | text body + If-Match on the draft file; a successful write **clears that scene's verify findings** (see below) |
| `GET /api/stories/{sid}/verify/{variant}` | parsed `verify.<variant>.md` |
| `POST /api/stories/{sid}/bible/{variant}/approve` | `{acceptedLineIds[]}` → appends to universe `bible.md` under dated heading, rewrites pending file, atomically |
| `GET /api/stories/{sid}/audio/{variant}` | mp3, `enableRangeProcessing: true` (phone seek depends on it) |

**Verify-flag lifecycle**: findings describe the prose they were written against, so any rewrite
of a scene invalidates that scene's findings. Scene PUT and a successful `regenScene` job both
remove the `## <sceneId>` section from `verify.<variant>.md` (other sections, incl. `## global`,
keep their exact bytes; the file is deleted when no sections remain). Findings are never
regenerated implicitly — the user re-runs verification explicitly via the `verify` job. The full
`generate` pipeline skips the per-scene clear: its verify stage rewrites the whole report anyway.

### Jobs
| Route | Notes |
|---|---|
| `POST /api/stories/{sid}/jobs` | `{type: "transcribe"\|"generate"\|"regenScene"\|"verify"\|"renderTts", variant, sceneId?, feedbackNote?, file?}` → `202 {jobId}` |
| `GET /api/jobs` | all recent jobs: state, stage, storyId, variant |
| `GET /api/jobs/{id}` | + log tail (last ~100 lines), cost-so-far, elapsed |
| `POST /api/jobs/{id}/cancel` | kills process tree / disposes model sessions |

Jobs run **strictly serially** (one GPU). In-memory only; the durable record is `gen.<variant>.json`
appends in the story folder. SPA polls `GET /api/jobs` every 2 s while any job is running.
GPU residency: model sessions are lazy-loaded per job and disposed at job end — Whisper (~3 GB)
and Chatterbox (~2.5–3 GB) must never coexist in VRAM.

### Status
`GET /api/status` → `{claude: {found, version}, gpu, models: {whisper, chatterbox}, libraryRoot}`.

## claude -p invocation (verified against CLI 2.1.170, 2026-08-08)

- cwd = **repo root** (so `skills/story/`, `library/universes/`, `library/stories/` are all reachable without `--add-dir`).
- Shape: `claude -p "Stage: <stage>. Story: library/stories/<slug>. Variant: <v>. [Scene: sN.] [Feedback: <note>.] Follow skills/story/SKILL.md." --output-format stream-json --verbose --permission-mode acceptEdits --allowedTools "Read,Write(library/stories/<slug>/**)" --max-budget-usd <cap>`
- ⚠ **`--max-turns` does not exist in 2.1.170.** Runaway protection = `--max-budget-usd` (default cap 0.50/stage, config) + per-job-type process timeout + `Process.Kill(entireProcessTree: true)`.
- `--verbose` is required with `--output-format stream-json` in print mode.
- **Never `--bare`**: it would bypass the subscription login (billing switch to API key) and skip skill discovery.
- Scene/regen stages write to scratch `scene.sN.out.md`; the **app** splices into `draft.<v>.md` atomically with re-read. The skill never edits the draft directly.
- Sessions: `outline` stores its `session_id` as `draftSessionId` in `gen.<v>.json`; `scene sN` uses `--resume <draftSessionId>`; `regenScene` is **always a fresh session**. Resume failure → fresh fallback reading outline + draft-so-far.
- Final `result` message fields used: `session_id`, `total_cost_usd`, `is_error`, `result`.

## Job → stage mapping

| Job type | claude stage(s) | Writes |
|---|---|---|
| `transcribe` | — (Whisper.net) | `recollections/<person>.txt` |
| `generate` | extract → outline → scene s1..sN → verify → bible | session (empty fields only), outline, draft (via scratch-splice), verify.md, bible.pending.md |
| `regenScene` | regen sN (fresh session) | scratch → splice; previous block saved to `draft.<v>.sN.prev.md`; clears the scene's `verify.<v>.md` section |
| `verify` | verify | `verify.<v>.md` |
| `renderTts` | — (Chatterbox ONNX) | `audio/<v>.mp3` |
