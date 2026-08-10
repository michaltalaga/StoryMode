# Session Stories — Project Handover

Context doc for Claude Code. Read this first.

## What this is

A local pipeline that turns loose recollections of a game session — RPG, board game
battle report, or just a rough story premise — into a listenable narrated story.
Audio is the product. Video is not planned.

Primary audience: me and my two kids. The kids will provide their own recollections
of the same session, each producing a separate story from their character's POV.
This is the strongest part of the idea and the design should protect it.

Not a publishing project. No empire. Scale is "a few stories a month, listened to
in the car."

## Core design decision

**Input capture is AI-free. Generation is triggered, not typed. TTS is automated.**

A form (eventually a small web panel) writes plain files into a project folder.
A skill does the AI generation — invoked by hand at first, later via
`claude -p` headless (see below). A separate script turns the resulting text
into an mp3.

Everything lives on disk as inspectable files. No database, no orchestration
framework, no state outside the filesystem.

## Folder structure

```
stories/
  2026-08-08-marrowfield/
    session.json          <- form output: toggles, cast selection, POV
    recollections/        <- raw notes, voice notes, transcripts
      michal.txt
      kid1.m4a
      kid1.txt            <- whisper output, generated
    outline.md            <- generated
    draft.md              <- generated
    audio/
      story.mp3

universes/
  generic-fantasy/
    constraints.md        <- hand-written, see below
    bible.md              <- accumulates across sessions
    characters.md         <- recurring cast
  grimdark/
    ...
```

(Voices turned out to be global, not per-universe — see the addendum.)

`session.json` should be hand-editable. If the panel ever becomes a bottleneck,
I want to skip it and edit JSON directly. Keep it that dumb.

Per-kid stories are multiple `session.json` variants inside one story folder,
sharing the same `recollections/`.

## Pipeline

```
raw recollections
  -> (whisper, if audio)
  -> story spec: cast, stakes, ordered beats, given|invent flags
  -> + universe constraints + bible
  -> outline (scenes: POV, beat, target length)
  -> prose, scene by scene
  -> draft.md
  -> [human read]
  -> TTS
  -> mp3
```

Two loops matter:
- regenerate a **single scene** without rerunning everything
- write new facts back to `bible.md` after each story

### given vs invent

Every beat carries a flag. A battle report is nearly all `given` — the events
actually happened and must not be rewritten. A rough premise is nearly all
`invent`. RPG sessions are mixed.

This flag is what stops the model from rewriting my actual game, and what lets
it genuinely create when I want it to. Get it right early.

### Kids' recollections

Kids remember what was exciting, not what happened. Wrong order, missing
connective tissue, disproportionate emphasis. **This is not a defect — it is a
POV.** Do not normalize it. If a kid spends four sentences on the goblin fight
and one on the entire cave, the story weights it that way.

Do not clean transcripts. The messiness is the input.

## TTS — settled

**Model: Chatterbox (Resemble AI).** MIT licensed, `pip install chatterbox-tts`,
zero-shot cloning from ~5s reference. Has an exaggeration knob. Runs on CUDA,
~4-6GB VRAM.

**Voices: Kyutai CC0 voice donation set.**
https://huggingface.co/kyutai/tts-voices

These are voices people deliberately donated for synthesis — actual consent for
the actual use case. 228 verified voices, CC0. Use the `*_enhanced.wav` cleaned
versions. Take the **wavs only** — the embeddings in that repo are Kyutai-format
and not portable to Chatterbox.

Watch out: the EARS-derived voices in the same repo are CC BY-NC 4.0
(non-commercial). Check per-voice licensing.

**Why not LibriVox:** recordings are public domain, but voice/likeness rights are
a separate regime and are not waived by a PD dedication. In Poland that is
*dobra osobiste* (Art. 23 KC), which explicitly covers voice and is
non-transferable. A volunteer donating a Frankenstein reading did not consent to
becoming a synthetic narrator.

**Do not load community `.pt` files.** They are pickle — arbitrary code execution
on load. Also unverifiable provenance. If a community voice pack looks good, take
the source wav and run `prepare_conditionals` locally.

**Conditionals caching:** `model.prepare_conditionals(wav_path, exaggeration=...)`
builds a `Conditionals` object with `.save()` / `.load()`. Compute once per voice,
reuse forever. Faster, and guarantees identical conditioning across episodes —
which matters for a consistent narrator over hours of output.

Note: exaggeration is baked in at prepare time. Separate saved file per setting.

**Starting params for narration:** exaggeration ~0.6-0.7, cfg_weight ~0.3.
Lower cfg slows delivery, which is most of what makes narration sound weighty.

**Test on a full 5-minute passage, not a demo sentence.** Drift and prosody
flattening only show up at length.

## Lore grounding

Two layers, and the second one is the one that matters:

**Constraint document** (hand-written, per universe, ~1 page):
- voice register
- naming conventions per faction
- hard rules (what cannot happen in this world)
- forbidden anachronisms — the "okay" and "guys" problem
- scale calibration

This catches roughly 80% of what breaks immersion. Write it first.

**Retrieval corpus** (optional, later): chunked wiki/codex material, scoped to
the factions actually in play. Pulling unrelated faction lore in is worse than
pulling nothing.

**Verification pass:** a separate read-the-draft-and-flag-violations step is
cheaper and catches more than trying to prevent violations upfront.

## Tone variants

The same story spec can produce multiple outputs — e.g. a grimdark version and a
kid-friendly version. Beats stay fixed; register, naming, stakes framing,
consequence handling, and ending posture come from the universe pack.

**Generate twice from the shared spec. Do not translate one output into the
other** — translation drags the source tone along and you get a cheerful story
with grim bones.

Different TTS voice per variant. Same script, different conditionals file.

## Target register

This sample was generated in chat and approved. It is the reference for what
"good" sounds like — third person close, dry, character revealed through
behaviour rather than description.

---

The elder of Marrowfield did not offer them chairs.

He stood in the doorway of his own house with the door half-closed behind him,
and he spoke about the gem the way a man speaks about a debt he has decided not
to pay. It had been taken. It was in the caves above the treeline. The village
would be grateful.

"Grateful," said Vesh, who had been a mercenary long enough to know what the word
cost.

"Grateful," the elder agreed.

Behind him, Corin was already looking at the mountain. It was the wrong shape for
a mountain — too flat at the top, as though something had sat on it. He had a
habit of noticing the wrong things first and being right about them later, which
his companions found less charming than he did.

---

The third trap was the one that got them.

The first two had been honest — a pressure plate, a drop of stone, the sort of
thing a careful party survives by being careful. Corin had found both. He was
pleased with himself about it, and being pleased with himself was, in the end,
the trap.

He put his hand on the wall to steady his lamp and the wall gave way behind his
fingers like wet paper, and then he was gone, and there was a sound from below
that was not a landing.

---

For kids' stories: same craft, different consequence handling. The fall still
happens, it just does not sound like that.

## Headless invocation

Claude Code runs non-interactively with `-p` / `--print`: one prompt in, one
result out, process exits with a status code.

```bash
claude -p "Generate the next scene per SKILL.md" \
  --allowedTools "Read,Write" \
  --permission-mode acceptEdits \
  --output-format json \
  --max-turns 8
```

Notes:
- Keep tool permissions narrow. Unattended runs should never need Bash.
- `--output-format json` returns `session_id`, `total_cost_usd`, `result`.
  Log cost per story; it should be trivial but worth knowing.
- `--resume <session-id>` continues a run — useful for scene-by-scene generation
  where each scene needs the previous one as context.
- The prompt must stand on its own. No human to clarify mid-run.

This is what lets the web panel have a "generate" button instead of me driving
each step by hand. Same skill, same files — just triggered.

Docs: https://code.claude.com/docs/en/headless

## Build order

Backwards, because the end of the pipeline is the part that can be verified.

1. **TTS script.** Text file in, mp3 out. Chatterbox, chunking, stitching,
   normalize, trim silences. Nothing else. If this does not sound good, nothing
   else matters. ~2 hours.
2. **Constraint doc** for one universe. Hand-written. Chat work, not code.
3. **Generation by hand in chat.** No skill, no automation. Iterate the prompt
   until output matches the sample above. This is where quality is determined.
4. **Turn the working prompt into a skill.** Folder structure follows from what
   the prompt actually needs, rather than being guessed upfront.
5. **Whisper transcription.** `faster-whisper`, large-v3, GPU. Handles Polish and
   English. Only needed once kids are actually recording.
6. **Web panel.** Last. Build when hand-editing JSON becomes annoying, not before.
   Wire its generate button to `claude -p` rather than reimplementing anything.

Steps 2 and 3 are not coding tasks and should not be automated early. Automating
before knowing what good looks like means automating the wrong thing.

## Open questions

- Input schema still needs work. The universal core is: universe, cast, stakes,
  ordered beats, outcome. Adapters feed it from battle reports / RPG sessions /
  premises. Not yet designed properly.
- RPG sessions need a "what to skip" field — sessions contain rules arguments and
  snack breaks.
- `bible.md` will grow and start eating context. Needs periodic pruning: drop
  details that never recurred, keep what does. Do not design the structure
  upfront; let it accumulate first.

## Environment

Windows, C#/.NET primary, comfortable with Python, Docker, PowerShell. CUDA GPU
available. ONNX Runtime already working locally.

## Legal note

If a Games Workshop universe is ever used: GW's IP policy permits non-commercial
fan work only. Fine privately; monetization is not.

## Addendum — corrections discovered during the build (2026-08-09)

Everything above is the original handover. The build (see `docs/api.md`, the living
contract) invalidated a few details:

- **Exaggeration is a runtime input** in the Chatterbox ONNX export (`embed_tokens`
  takes it per call), not baked at prepare time. One conditionals cache per voice
  serves all exaggeration settings — the "separate saved file per setting" note above
  is obsolete.
- **TTS runs fully in .NET** (C# port of `onnx-community/chatterbox-multilingual-ONNX`
  on ONNX Runtime CUDA, token-parity-proven against the Python reference —
  `docs/reference/parity-results.md`). No Python in the pipeline; whisper runs via
  Whisper.net.
- **Variant files are always `session.<variant>.json`** — the bare `session.json` in
  the folder sketch above is invalid; the variant name comes from the filename.
- **`claude` 2.1.170 has no `--max-turns`** — runaway protection is `--max-budget-usd`
  plus per-job process timeouts. `--verbose` is required with `stream-json`. Never
  `--bare` (it switches billing off the subscription login).
- **CUDA 13 runtime is staged repo-locally** in `models/cuda` (no system install);
  the code prepends it to the process PATH automatically.
- **Voices are global, not per-universe** — the catalog lives at `library/voices.json`
  with reference wavs in `library/voices/` and rendered samples in
  `library/voice-previews/`. A voice is an engine/hardware concern shared by every
  story world; `session.<v>.json` still names which voice a variant uses.
- **A voice is an installed artifact, not a config row.** The first cut modelled a
  voice as the engine plus its raw knobs, which forced the reader to be the
  integration layer: pick an engine, supply a wav, invent numbers for `exaggeration`
  and `cfg`. That is unusable, and it was a missing abstraction rather than a UI
  problem. There are now three concepts where there was one: an **installed voice**
  (human name, locale, named delivery, engine-private `engineData`), a **shelf** of
  installable offers (`IVoiceGallery`, curated in `<repo>/voice-gallery/`), and a
  per-engine **installer** (`IVoiceInstaller`) that does whatever that engine needs.
  Adding an engine means registering an installer — nothing else, and no
  configuration file, learns about it. In particular the piper bundle name moved out
  of `appsettings.json` and onto the voice, because a voice you cannot add without
  editing config is a voice the app cannot really offer.
- **Delivery is a named style, never a number — and it belongs to the story.**
  `calm`/`natural`/`lively` lives in `session.<variant>.json` as `delivery`. It was on
  the voice first, which was wrong: a voice is *who is speaking* and stays put, while
  delivery is *how this story is read* — the same narrator wants different pacing for
  a battle report and a bedtime story. On the voice it forced one delivery across
  every story that voice narrated, and silently staled the voice's sample every time
  it changed. Each provider declares the same three ids over whatever knobs it
  actually has, and the job runner resolves them against the chosen voice's engine at
  render time, so two engines with unrelated knobs (`exaggeration`+`cfg` vs `speed`)
  share one control and the numbers stay backstage. A voice's sample is always the
  neutral delivery, which is why it cannot go stale.
- **"No Python" meant "not on the host", not "never".** The rule was read too strictly
  for months: it turned every candidate engine into an ONNX-port question, and the
  only modern model we had was the one hand-ported at great cost. The escape hatch was
  granted at the time — *"if necessary we can either find an mcp or a docker that can
  be used instead"* — and a container satisfies it exactly. `xtts-docker` runs XTTS-v2
  that way: seventeen properly-trained languages including Polish, no Python on the
  host, one more `ITtsProvider`. Its licence (Coqui Public Model License,
  non-commercial) keeps it unregistered until accepted by hand.
- **Recording a voice needs https, so the app serves both.** Browsers hide the
  microphone on an insecure origin, which made "record grandma reading" impossible
  over the plain-http LAN address. Kestrel now also listens on `:5212` with the
  ASP.NET dev certificate. It is issued for `localhost`, so a phone reaching the box
  by hostname warns once and must be waved through; after that the origin is secure
  and capture works. The capture path encodes wav in the browser rather than
  server-side, because Android records webm/opus and Windows Media Foundation cannot
  decode it — the browser can always decode what it just recorded.
- **Samples are an install-time obligation.** An install is not finished until the
  voice has a playable mp3 on disk, so pressing play is a static file read and never
  a render — the earlier design had play trigger a multi-minute synthesis. Shelf
  samples are pre-rendered by the real engine and committed, so you can hear a voice
  before downloading it, and installing one is a copy.
