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

Piper also serves coqui VITS bundles: they ship no `espeak-ng-data` and are character-based
rather than phoneme-based, which the provider used to reject outright.

Measured on an RTX 4060 Ti, cross-lingual (English reference reading Polish): XTTS renders at
**3.2× realtime**. Chatterbox runs near realtime and doubles its own work for guidance.

## Candidates, not yet tried

| Engine | Licence | Polish | Size | Why it is interesting |
|---|---|---|---|---|
| **MOSS-TTS** | **Apache 2.0** | yes, 20 langs | 4B (Qwen3 backbone) | The only permissive licence of the lot. v1.5, June 2026. `MOSS-TTS-Nano` is small enough for CPU |
| **Higgs TTS 3** | research / non-commercial | yes, 100+ langs, WER/CER < 5 | 4B | Best claimed Polish. Served via SGLang-Omni, so a heavier image |
| **VibeVoice** | Microsoft | community packs (`Sticzu/vibevoice-polish-voices`) | 1.5B / 0.5B realtime | Built for **long-form** — ~90 min single pass. Aimed at the drift problem rather than the language problem. Polish is "provided to explore", not benchmarked |
| **LLaSA Polish** | per fine-tune | `salihfurkaan/VoxPolska-Auralis` is Polish-only | 1B | A dedicated fine-tune usually beats a multilingual model that merely lists the language |
| **VoxCPM2** | check | yes, of 30 langs | small | Tokeniser-free, April 2026 |
| **Qwen3-TTS** | check | unverified | — | Requested; not yet researched |

Ruled out, with the reason:

- **Kokoro** — clearly better than Piper, already packaged in sherpa-onnx, but **no Polish**
  (8 languages, and Polish is not among them).
- **F5-TTS, Zonos** — actively developed, but Polish needs fine-tuning rather than being native.
  A community `Gregniuki/F5-tts_English_German_Polish` exists if that changes.
- **ZipVoice** — zero-shot cloning, already in sherpa-onnx as ONNX, but zh/en only.

## How to add one

1. `docker/<name>/` — a Dockerfile and a server answering `/health`, `/prepare`, `/synthesize`.
   Copy `docker/xtts/` and swap the model; the contract is deliberately tiny.
2. Register a `ContainerTtsEngine` in `Program.cs` with its languages and licence note.
3. `scripts/build-tts-images.ps1 <name>`.

No installer, no UI, no store changes: they all clone from a recording, so `CloningVoiceInstaller`
serves every one of them unchanged.

## What actually decides this

Not integration — listening. Two questions settle it:

1. **Is Polish usable at all on this hardware?** XTTS answers that; if its Polish is good, the
   remaining work is choosing a licence-clean equivalent (MOSS) rather than hunting for quality.
2. **Does it hold together over twenty minutes?** Short samples always sound fine. The failure
   that killed chatterbox appeared past the one-minute mark, so any candidate must be judged on a
   long passage, chunked exactly as a real story renders.
