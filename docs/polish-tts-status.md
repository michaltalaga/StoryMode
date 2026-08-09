# Polish TTS — parked (2026-08-09)

**Status: Polish narration is parked as unusable. English (Chatterbox, user-approved) is the
working configuration. This note is the runway for resuming.**

Proof it is possible: a 2-hour Polish narration on par with our English quality exists —
https://www.youtube.com/watch?v=RP8cUaGOn5g (Chatterbox multilingual via Chatterbox-TTS-Server,
PyTorch, reference voice "Kustosz.wav", cfg 0.5 / temp 0.7 / exagg 0.5 / chunk 200 chars; source:
r/LocalLLaMA thread by its author, who still reports occasional growl artifacts + long pauses).

## What was established (with evidence)

1. **The onnx-community multilingual export is broken for Polish**: its `embed_tokens` table has
   2352 rows vs the tokenizer's 2454-token vocab — 102 truncated embeddings hitting late-vocab
   entries (Polish diacritic merges, typographic punctuation). Reproduced locally: token id 2363
   crashes the Gather node; corroborated by onnx-community issues #5/#7 and
   resemble-ai/chatterbox#311 (Polish-only accent, demo fine / local bad).
2. **`Folx/chatterbox-ONNX-polish` is the corrected export** (MIT, fp32 LM). Downloaded to
   `models/chatterbox-pl/` (gitignored). Its own demo (`example_output_polish.wav`) transcribes
   flawlessly under whisper large-v3.
3. **It must be fed exactly as trained** (its `test_polish.py`): text `lower()` + `NFKD`
   decomposition, temperature 0.3, repetition penalty 2.0, min_p 0.05, **no CFG**. Our port
   renders it via `tts-cli synth --model-dir models/chatterbox-pl --cfg 0 --temperature 0.3
   --exaggeration 0.5 --rep-penalty 2.0` with pre-normalized text. This fixed the diacritics
   (verified by large-v3 transcription: końcu/wszędzie/wioska correct).
4. **Remaining failures at that best-known config** (user verdict 2026-08-09): still clearly
   below the English bar; growl/"exorcism" artifacts (~60-70s region); language drift toward
   Czech on long generations; English character names read phonetically-Polish. These match the
   community-reported long-form instability of the model's thin Polish slice.
5. **Piper (sherpa-onnx, pl_PL gosia/darkman)** stays wired as the routable Polish fallback:
   phonetically correct, prosodically flat ("nowhere near EN"). voices.json routes
   `narrator-pl-*` → `piper-onnx`.

## Next rungs when resumed (in rough order)

- **Study the YouTube pipeline delta**: same model family reaches the bar in PyTorch serving —
  candidates for the gap: their chunking (200 chars), crossfade stitching, loudness norm of the
  prompt, the Kustosz reference voice itself, retry-on-artifact behavior of Chatterbox-TTS-Server.
- **Chunk QA loop (engine-agnostic, buildable any time)**: render chunk → whisper round-trip →
  gross transcript mismatch ⇒ re-seed and retry. Auto-catches growls on any engine.
- **XTTS-v2 docker sidecar**: cloning + native Polish; CPML non-commercial (private ok, never
  bundle weights publicly); serving via daswer123/xtts-api-server built from source.
- **Cloud fork (user's privacy call)**: ElevenLabs / Azure (pl-PL neural voices) — research was
  in flight when parked; re-run the `polish-tts-next-rung` workflow for the decision brief.
- **Own finetune** of Chatterbox on Polish (gokhaneraslan toolkit + VladOS ONNX export recipe) —
  weeks; last resort.

## Assets on disk

- `models/chatterbox-pl/` — corrected Polish export (re-download: HF `Folx/chatterbox-ONNX-polish`;
  note `language_model.onnx` is fp32 and is aliased as `language_model_fp16.onnx` — the port
  detects precision from metadata, the filename is just the default lookup name).
- `library/universes/generic-fantasy/voices/narrator-pl-{gosia,darkman}.wav` — OHF CC0 references.
- `models/pl-fixed-matched.mp3` — the best-achieved Polish render (the one that was rejected).
