# TTS candidates

Every engine considered for this project, why, and what is actually known about it. Written
because the survey kept being redone from memory in conversation and kept missing things —
Higgs, MOSS and the high-quality Piper tiers were all found late, after decisions had already
been argued on incomplete information.

**The constraint that shaped everything:** no Python on the host. For months that was read as
"every engine must be an ONNX port", which is why chatterbox was hand-ported at the cost of weeks
and why Polish stayed broken. The actual instruction allowed a container. Since
`ContainerTtsProvider` exists, **adding an engine is a Dockerfile plus one registration entry** —
so the cost of trying one is an afternoon, and the real bottleneck is listening, not integrating.

## In the app now

| Engine | Licence | Polish | Clones | Where it runs |
|---|---|---|---|---|
| `chatterbox-onnx` | MIT | poor — drifts, slides toward Czech | yes | in-process, CUDA |
| `piper-onnx` | MIT / CC0 per voice | correct pronunciation, flat delivery | no | in-process, CPU |
| `xtts-docker` | CPML — **non-commercial** | yes, native | yes | container, CUDA |
| `qwen-container` | **Apache 2.0** | yes, and only Polish | **no** | container, CUDA |
| `moss-container` | **Apache 2.0** | yes, 20 langs | yes | container, CUDA |

Built but not yet heard, so switched off in appsettings — an engine that has never made a sound
must not appear as something to pick:

| Engine | Licence | Polish | Clones |
|---|---|---|---|
| `higgs-container` | research — **non-commercial** | yes, of 102 langs | yes |
| `vibevoice-container` | MIT | no — English and Chinese | yes |
| `vibevoice-pl-container` | MIT | two fine-tuned voices | **no** |

Measured on an RTX 4060 Ti, cross-lingual where the reference is English reading Polish:

| Engine | Realtime factor | A twenty-minute story |
|---|---|---|
| `piper-onnx` | 39.6× | about 45 seconds |
| `xtts-docker` | 3.2× | about 6 minutes |
| `moss-container` | 0.75× | about 27 minutes |
| `qwen-container` | 0.15× | over two hours |

`chatterbox-onnx` has never been timed properly; a nine-second preview took about four minutes
during voice installs, so it belongs with Qwen rather than with XTTS.

### What each one turned out to be

**`qwen-container`** runs `agnostic/Qwen3-TTS-Polish`. Three things about it are not what the
repository name suggests, all found by asking the installed package rather than reading the card:

- It is a **CustomVoice** checkpoint, so it **does not clone**. `generate_voice_clone` raises, and
  the speaker-encoder weights are discarded at load. It ships exactly one voice, `polish_speaker`.
- The library's language whitelist contains no Polish — `['auto', 'chinese', 'english', 'french',
  'german', 'italian', 'japanese', 'korean', 'portuguese', 'russian', 'spanish']` — even though the
  checkpoint is Polish-trained. `language="auto"` is the way in.
- It is **slow**: 32 s of audio took 216 s. flash-attn is not installed in the image, which may
  account for some of it.

**`moss-container`** cost three separate fixes before it made a sound, none of them in the model:

- `torchaudio.load` in torchaudio 2.9 forwards to torchcodec, whose shared objects link against
  FFmpeg 7. Ubuntu 24.04 — the newest base NVIDIA publishes for CUDA 12.8 — ships FFmpeg 6.1.
  Installing the `-dev` packages does nothing: the mismatch is the soname. The image patches
  `torchaudio.load` to read through libsndfile before the checkpoint's remote code imports it.
- `decode()` returns **waveforms** despite the field being called `audio_codes_list`, and stereo
  ones by default. Writing those as mono float32 is what libsndfile was rejecting.
- Short lines — a one-word answer in dialogue — do not reliably make it emit an end token. It
  stalls mid-line and resumes after a gap of seconds, and with a flat token ceiling it can spend
  twenty minutes on one line. The image scales the ceiling with the text and closes over-long
  internal silences rather than cutting at them, because measurement showed the audio after the
  gap is the rest of the sentence, not a hallucination.

**`higgs-container`** cannot be served the way Boson serve it. `sglang-omni` depends on
`flash-attn-4` and CUDA 13 wheels, and flash attention 4 needs Hopper or newer; this machine has
an Ada card. `vllm-omni` requires `fa3-fwd`, which has the same problem. What works instead is
`multimodalart/higgs-audio-v3-tts-4b-transformers` — a `trust_remote_code` repackaging of the
identical weights, a Qwen3-4B backbone with a fused multi-codebook audio head, that loads on plain
transformers ≥ 5.5 exactly like MOSS does. Its licence permits monetised creator use provided
Boson AI's Higgs Audio is credited, but not production or hosted services.

**`vibevoice-container`** is two engines sharing one image. Microsoft withdrew the TTS inference
code in September 2025 after finding it misused and restored the repository without it; the
community fork carries it under MIT and the weights survive on the Hub. `VibeVoice-1.5B` clones a
recording and holds up to ninety minutes in a single pass — the only engine here built for
long-form, so it is chunked by paragraph rather than by sentence. Polish exists only as two voices
fine-tuned onto `VibeVoice-Realtime-0.5B`, which has no speaker encoder, so those are a separate
fixed-voice engine rather than one entry that sometimes ignores your recording. The Polish voices
ship as pickled cache objects, which `torch.load` executes; that happens inside the container from
one pinned repository.

`piper-onnx` also serves coqui VITS bundles: they ship no `espeak-ng-data` and are character-based
rather than phoneme-based, which the provider used to reject outright.

## Candidates not built

| Engine | Licence | Polish | Clones | Why not |
|---|---|---|---|---|
| **VoxPolska-Auralis** | **Apache 2.0** | Polish-*only* fine-tune | no | 1B on Llama-3.2 + xcodec2. A whole model spent on one language, but it outputs 16 kHz, and MOSS now covers the permissive-and-Polish case |
| **VoxCPM2** | check | yes, of 30 langs | yes | Tokeniser-free, April 2026. Not researched in detail |

### Ruled out, with the reason

- **Kokoro** — clearly better than Piper and already packaged in sherpa-onnx, but **no Polish**
  (8 languages, Polish not among them).
- **F5-TTS, Zonos** — actively developed, but Polish needs fine-tuning rather than being native.
  A community `Gregniuki/F5-tts_English_German_Polish` exists if that changes.
- **ZipVoice** — zero-shot cloning, already in sherpa-onnx as ONNX, but zh/en only.
- **Qwen3-TTS base** — ten languages, Polish not among them; superseded by the Polish fine-tune.

## How to add one

1. `docker/<name>/` — a Dockerfile and a server answering `/health`, `/prepare`, `/synthesize`.
   Copy `docker/moss/` or `docker/higgs/` and swap the model; the contract is deliberately tiny.
2. Add a `ContainerEngineOptions` property to `SessionStoriesOptions`, one
   `ApplyContainerDefaults(...)` line, and a `ContainerTtsEngine` registration in `Program.cs`
   with its languages and licence note.
3. Add it to `KnownEngines` so Settings shows it whether or not it is switched on.
4. `scripts/build-tts-images.ps1 <name>`, then set `<Name>:Enabled` in appsettings.
5. Shelf entries in `voice-gallery/manifest.json`, with a sample rendered by the real engine:
   `tts-cli synth-container --engine <name> --port <n> ...`.

No installer and no store changes: `CloningVoiceInstaller` serves every engine that clones and
`FixedVoiceInstaller` every engine that does not.

## What actually decides this

Not integration — listening. Two questions settle it:

1. **Is Polish usable at all on this hardware?** XTTS and MOSS both answer it in principle; which
   of them is worth living with is an ear question, and MOSS is the one whose licence survives the
   repository going public.
2. **Does it hold together over twenty minutes?** Short samples always sound fine. The failure
   that killed chatterbox appeared past the one-minute mark, so any candidate must be judged on a
   long passage, chunked exactly as a real story renders.
