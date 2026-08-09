# Session Stories — API & Invocation Contract

**This file is the single authority** for routes, ports, folder paths, and the `claude -p`
invocation shape. If code or plan disagrees with this page, this page wins; change it here first.

## Ports & paths

- API + SPA: `http://0.0.0.0:5211` and `https://0.0.0.0:5212` (Kestrel, LAN; firewall Private profile; no auth v1 — all routes under `/api` = future auth seam).
- **https exists for exactly one reason**: browsers expose the microphone only on a secure origin, so recording a voice from a phone is impossible over plain http. Kestrel serves it with the ASP.NET developer certificate (`dotnet dev-certs https`), which is issued for `localhost` — a phone reaching the box by hostname gets a name-mismatch warning and must continue past it once per device. After that the origin is secure and the microphone works. Everything else is happier on `:5211`.
- Vite dev proxy: `/api → http://localhost:5211`.
- `LibraryRoot` default: `<repo>/library` (contains `stories/`, `universes/`, `voices.json`, `voices/`, `voice-cache/`, `voice-previews/`). Overridable in `appsettings.json`.
- **Voices are global**, not per universe: the catalog is `library/voices.json`, reference recordings are bare file names under `library/voices/`, and each voice's sample lives at `library/voice-previews/<voiceId>.mp3`. Wavs, samples and the conditionals cache are gitignored; the catalog JSON is committed.
- **The shelf is app content**, not library content: `<repo>/voice-gallery/` holds `manifest.json`, `samples/` (pre-rendered, committed) and `wavs/`. It ships with the code because only the engines in this build know how to install what it lists, so it is unaffected by where a reader points `LibraryRoot`. Override with `SessionStories:GalleryRoot`.
- Models: `<repo>/models` (`chatterbox/`, `whisper/`, `piper/`). Downloaded by `scripts/download-models.ps1`, never committed.
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
| `GET/PUT /api/universes/{uid}/files/{name}` | `constraints.md`, `bible.md`, `characters.md`, `tones/{tone}.md` (name may contain one `/` for tones) |
| `GET /api/universes/{uid}/pending-facts` | aggregated `bible.pending.*.md` across that universe's stories → `[{storyId, variant, lineId, text, sceneId}]` |

### Voices
Global — a machine/engine concern, shared by every universe. A voice is an **installed artifact**
(human name, locale, named delivery, plus whatever private assets its engine needed), not a config
row someone writes by hand. **Which engine backs a voice never crosses this boundary**: no route in
this section accepts or returns an engine id, a knob value or a file name.

**Three invariants make the play button instant** — the whole point of the model:
1. An install is not complete until `library/voice-previews/<id>.mp3` exists (the install job's last step).
2. Playing a voice is a plain `GET` of that static file and **never enqueues a job**.
3. Shelf samples are pre-rendered by the real engine and committed, so auditioning before installing
   is instant and offline — and installing a shelf voice *copies* that sample instead of rendering one.

Installed voices:

| Route | Notes |
|---|---|
| `GET /api/voices` | `[{id, name, description, locale, style, styles[], isDefault, hasSample, attribution, license}]` — `[]` when no catalog. `id` is the stable key stories point at and is never displayed; `style` is one of `styles` |
| `PATCH /api/voices/{id}` | `{name?, description?, style?}` — the only fields a reader may change. `200` with the item; 400 on an empty name or unknown style, 404 when not installed |
| `DELETE /api/voices/{id}?deleteWav=` | `204`. Removes the entry, its `library/voice-previews/<id>.mp3` and its `library/voice-cache/<id>` folder, and clears `default` when it pointed here. The reference recording may back several voices, so it stays unless `deleteWav=true`. 404 when absent |
| `PUT /api/voices/default` | `{id}` → `204`; 404 when not installed |
| `GET/PUT /api/voices/catalog` | raw `library/voices.json` text, ETag / If-Match like the universe files (428 without `If-Match`, 409 + current content on mismatch) — the escape hatch |
| `GET /api/voices/{id}/preview` | the voice's sample mp3, `enableRangeProcessing: true`. Invariant 2 lives here: this is a static file, always present after a successful install |
| `POST /api/voices/{id}/preview` | `202 {jobId}` — re-records the sample (a `previewVoice` job). **Not** what the play button does; 404 when not installed |

The shelf (voices you could install), from `<repo>/voice-gallery/manifest.json`:

| Route | Notes |
|---|---|
| `GET /api/voice-gallery/languages` | `[{locale, offerCount, canUpload}]` — step one of "add a voice". `canUpload` is true when some installed engine can clone a recording in that language |
| `GET /api/voice-gallery?locale=` | `[{key, name, description, locale, downloadBytes, license, attribution}]`. `downloadBytes` is what a **fresh** machine fetches; an asset already on disk is reused and the install is instant |
| `GET /api/voice-gallery/sample/{**key}` | the pre-rendered sample, range-enabled. Catch-all route: an offer key carries a slash (`piper/pl_PL-gosia`) |
| `POST /api/voices/install` | `{key, name?}` → `202 {jobId, voiceId}`, an `installVoice` job. 404 when the key is not on the shelf |
| `POST /api/voices/from-recording` | multipart (`file`, `name`, `locale`) → `202 {jobId, voiceId}`. Decodes wav/mp3/m4a (Media Foundation) to mono 24 kHz; rejects under 10 s up front with a 400 rather than after a queued job. The engine is chosen from the locale, never by the caller |

**Recording in the browser** (`app/src/components/recordWav.ts`) captures with `MediaRecorder`, then
decodes and re-encodes to mono 24 kHz PCM wav **client-side** before upload. That conversion is not
an optimisation: Android Chrome records webm/opus, which Windows Media Foundation — how the server
decodes — cannot read at all. The browser can always decode what it just recorded, so doing it there
removes the codec question entirely and the server only ever sees a wav. Expect roughly 8 minutes
end to end on an RTX 4060 Ti (conditioning, then the first sample render); the UI says so.

`voiceId` is slugified from the display name and uniquified; renaming a voice later never changes it,
so stories keep working. Reserved ids: `catalog`, `providers`, `default`, `install`, `from-recording`.

Structured edits round-trip the catalog through `JsonNode`, so unknown fields (`_notes`, hand-written
settings) survive; writes are atomic, same as every other store. **Schema**: `voices.json` is schema 2
(`name`/`description`/`locale`/`style`/`engine`/`engineData`/`source`). Schema 1 rows
(`provider`/`languages`/`referenceWav`/`exaggeration`/`cfg`) are read and mapped in memory — raw
exaggeration maps to the nearest named style — and any structured write converges that entry to
schema 2. A read never rewrites the file.

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
| `POST /api/stories/{sid}/jobs` | `{type: "transcribe"\|"generate"\|"regenScene"\|"verify"\|"renderTts", variant, sceneId?, feedbackNote?, file?}` → `202 {jobId}`. `previewVoice` and `installVoice` are rejected here — they belong to a voice, not a story (see Voices) |
| `GET /api/jobs` | all recent jobs: state, stage, storyId, variant, voiceId |
| `GET /api/jobs/{id}` | + log tail (last ~100 lines), cost-so-far, elapsed |
| `POST /api/jobs/{id}/cancel` | kills process tree / disposes model sessions |

Jobs run **strictly serially** (one GPU). In-memory only; the durable record is `gen.<variant>.json`
appends in the story folder. SPA polls `GET /api/jobs` every 2 s while any job is running.
GPU residency: model sessions are lazy-loaded per job and disposed at job end — Whisper (~3 GB)
and Chatterbox (~2.5–3 GB) must never coexist in VRAM.

**TTS engines**: `renderTts`, `previewVoice` and `installVoice` support multiple engines, selected
per voice from the `engine` field in `library/voices.json` (`chatterbox-onnx` when absent). Each
voice carries its engine's private settings in `engineData`, passed through to the provider as
`TtsRequest.EngineData`: `chatterbox-onnx` reads `referenceWav` (it clones), `piper-onnx` reads
`bundle` (Piper VITS via sherpa-onnx, CPU — fixed trained voices, no cloning, single `speed` knob,
native espeak-ng phonemization). That bundle name lives **on the voice**, which is what makes
UI-driven installs possible; it is deliberately no longer configured in `appsettings.json`.

**Delivery styles**: a voice stores a named style (`calm`/`natural`/`lively`), never raw knobs.
Each provider declares the same ids over whatever knobs it actually has
(`TtsCapabilities.StylePresets`), and the job runner resolves style → knobs at render time; a
session's `voiceOverrides` still wins on top, preserving the files-on-disk bypass. Two engines with
unrelated knobs therefore share one control, and the numbers never reach the client.

**Installers**: `IVoiceInstaller` per engine turns an offer (or a reader's recording) into an
installed voice — downloading, unpacking, converting and caching. Registering one is the only step
needed to add an engine; nothing else, and no configuration file, learns about it.

A story naming a voice that is not installed **fails loudly**, with a message listing what is
installed. Substituting another narrator silently would only be discovered by listening.

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
| `renderTts` | — (TTS provider per voice: Chatterbox ONNX or Piper/sherpa-onnx) | `audio/<v>.mp3` |
| `previewVoice` | — (same providers; fixed sample sentence chosen by the voice's primary language, en/pl) | `library/voice-previews/<voiceId>.mp3` |
