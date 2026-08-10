"""HTTP front for XTTS-v2. The only Python in this project, and it never leaves the container.

Two endpoints that matter:

  POST /prepare     compute a voice's speaker latents from a reference wav, once, and cache them
  POST /synthesize  render one chunk of text with a prepared voice

The split mirrors ITtsProvider on the host side: preparing is the expensive per-voice step and
caching it is what stops every render paying for it. Chunking, stitching and mp3 encoding all
happen on the host, so this stays a thin wrapper over the model and every engine in the app gets
the same post-processing.
"""

import base64
import io
import os
import threading
from pathlib import Path

import torch
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

MODEL_NAME = "tts_models/multilingual/multi-dataset/xtts_v2"
LATENT_DIR = Path(os.environ.get("XTTS_LATENTS", "/models/latents"))

app = FastAPI()

# One model, one lock. The host runs jobs strictly serially anyway, but a stray second request
# must queue rather than corrupt a generation half way through.
_lock = threading.Lock()
_model = None
_device = "cuda" if torch.cuda.is_available() else "cpu"


def model():
    """Loaded on first use rather than at import, so the container starts fast and /health answers."""
    global _model
    if _model is None:
        from TTS.api import TTS

        _model = TTS(MODEL_NAME).to(_device)
    return _model


def latent_path(voice_id: str) -> Path:
    # Voice ids are validated host-side, but this is a filesystem path in a server: check again.
    if not voice_id or "/" in voice_id or "\\" in voice_id or voice_id.startswith("."):
        raise HTTPException(status_code=400, detail=f"invalid voice id {voice_id!r}")
    return LATENT_DIR / f"{voice_id}.pt"


@app.get("/health")
def health():
    return {
        "ok": True,
        "device": _device,
        "modelLoaded": _model is not None,
        "cuda": torch.cuda.is_available(),
        "gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
    }


class PrepareRequest(BaseModel):
    voiceId: str
    referenceWav: str


@app.post("/prepare")
def prepare(request: PrepareRequest):
    wav = Path(request.referenceWav)
    if not wav.is_file():
        raise HTTPException(status_code=404, detail=f"reference wav not found: {wav}")

    with _lock:
        tts = model()
        gpt_cond_latent, speaker_embedding = tts.synthesizer.tts_model.get_conditioning_latents(
            audio_path=[str(wav)]
        )
        LATENT_DIR.mkdir(parents=True, exist_ok=True)
        target = latent_path(request.voiceId)
        tmp = target.with_suffix(".tmp")
        torch.save({"gpt_cond_latent": gpt_cond_latent, "speaker_embedding": speaker_embedding}, tmp)
        tmp.replace(target)

    return {"voiceId": request.voiceId, "cached": str(target)}


class SynthesizeRequest(BaseModel):
    voiceId: str
    text: str
    language: str
    temperature: float = 0.65
    speed: float = 1.0
    repetitionPenalty: float = 2.0


@app.post("/synthesize")
def synthesize(request: SynthesizeRequest):
    cached = latent_path(request.voiceId)
    if not cached.is_file():
        raise HTTPException(status_code=409, detail=f"voice {request.voiceId!r} has not been prepared")

    with _lock:
        tts = model()
        latents = torch.load(cached, map_location=_device, weights_only=False)
        result = tts.synthesizer.tts_model.inference(
            text=request.text,
            language=request.language,
            gpt_cond_latent=latents["gpt_cond_latent"].to(_device),
            speaker_embedding=latents["speaker_embedding"].to(_device),
            temperature=request.temperature,
            speed=request.speed,
            repetition_penalty=request.repetitionPenalty,
            enable_text_splitting=False,  # the host already chunked this
        )

    # Float32 mono at the model's own rate; the host resamples, stitches and encodes.
    import numpy as np
    import soundfile

    buffer = io.BytesIO()
    soundfile.write(buffer, np.asarray(result["wav"], dtype="float32"), 24000, format="WAV", subtype="FLOAT")
    return {
        "sampleRate": 24000,
        "wavBase64": base64.b64encode(buffer.getvalue()).decode("ascii"),
    }
