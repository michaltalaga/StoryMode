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

### Worth building

| Engine | Licence | Polish | Clones | Size | Why |
|---|---|---|---|---|---|
| **Qwen3-TTS Polish** | **Apache 2.0** | `agnostic/Qwen3-TTS-Polish` — a fine-tune that *adds* Polish | yes | **1.7B** | Smallest of the serious candidates, installs from PyPI (`qwen-tts`) rather than source, and the only one with a **C#/ONNX export** (`elbruno/ElBruno.QwenTTS`) — so if it wins it could eventually drop the container entirely. Revisions `-r9`, `-r10` exist |
| **MOSS-TTS** | **Apache 2.0** | yes, 20 langs, explicit language tag | yes | 4B local / 8B delay | The only permissive licence that also clones. `MOSS-TTS-Nano` (~100M) runs on 4 CPU cores |
| **Higgs TTS 3** | research / non-commercial | yes, 100+ langs, WER/CER < 5 | yes | 4B | Best *claimed* Polish of any of them. Served via SGLang-Omni, OpenAI-shaped `/v1/audio/speech` |
| **VoxPolska-Auralis** | **Apache 2.0** | Polish-*only* fine-tune | **no** | 1B (Llama-3.2 + xcodec2) | A whole model spent on one language. Fixed voice like piper, but LLM-based — may well beat it. Outputs 16 kHz, which is low |
| **VoxCPM2** | check | yes, of 30 langs | yes | small | Tokeniser-free, April 2026. Not yet researched in detail |

### Ruled out, with the reason

- **VibeVoice** — Microsoft **withdrew the TTS code** from the repository after finding it misused;
  only ASR and `VibeVoice-Realtime-0.5B` remain. The `Sticzu/vibevoice-polish-voices` packs target
  a model whose official inference code is gone. Its long-form design (~90 min single pass) was the
  genuinely interesting property, so revisit if the Realtime model proves capable in Polish.
- **Kokoro** — clearly better than Piper and already packaged in sherpa-onnx, but **no Polish**
  (8 languages, Polish not among them).
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
