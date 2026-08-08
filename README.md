# Session Stories

Turns loose family game-session recollections (RPG sessions, battle reports, rough premises)
into narrated mp3 stories. Local, file-system-first, LAN-only. Audio is the product.

Read first:
- [HANDOVER.md](HANDOVER.md) — the idea, the philosophy, the non-negotiables
- [docs/api.md](docs/api.md) — the single authority for routes, paths, and the `claude -p` contract

## Layout

```
src/SessionStories.Core/           domain + provider interfaces (no I/O)
src/SessionStories.Providers/      file stores, claude -p generator, Whisper.net STT
src/SessionStories.Tts.Chatterbox/ Chatterbox ONNX TTS port (C#, ONNX Runtime CUDA)
src/SessionStories.Api/            minimal API host, job runner, SPA hosting (:5211)
tools/tts-cli/                     text in → mp3 out; M1 harness and forever the panel-bypass path
app/                               React + Vite + TS SPA (phone-first capture, desktop review)
skills/story/                      the generation skill (editable content, iterated in chat)
library/                           stories/ + universes/ (text committed, audio/models ignored)
scripts/download-models.ps1        fetches Chatterbox ONNX + Whisper ggml into models/
```

## First run

```powershell
./scripts/download-models.ps1   # ~5.5 GB total
dotnet build
cd app; npm install
```
