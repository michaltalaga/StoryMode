"""HTTP front for Qwen3-TTS (Polish fine-tune), speaking the standard container contract.

Like MOSS and unlike XTTS there is no separate conditioning step to cache — the reference audio
goes in on every generation — so /prepare only records which recording belongs to a voice. Keeping
the endpoint anyway is what lets the host treat every container engine identically.

This engine clones markedly better when told what the reference recording *says*, so the transcript
travels with it when one is available.
"""

import base64
import io
import json
import os
import threading
from pathlib import Path

import torch
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

MODEL_NAME = os.environ.get("QWEN_MODEL", "agnostic/Qwen3-TTS-Polish")
VOICE_DIR = Path(os.environ.get("QWEN_VOICES", "/models/voices"))

# Qwen names languages in full rather than by code.
LANGUAGE_NAMES = {
    "pl": "Polish", "en": "English", "de": "German", "fr": "French", "es": "Spanish",
    "it": "Italian", "pt": "Portuguese", "ru": "Russian", "ja": "Japanese", "ko": "Korean",
    "zh-cn": "Chinese", "zh": "Chinese",
}

app = FastAPI()

_lock = threading.Lock()
_model = None
_device = "cuda" if torch.cuda.is_available() else "cpu"


def load():
    """Loaded on first use, not at import, so the container starts fast and /health answers."""
    global _model
    if _model is None:
        from qwen_tts import Qwen3TTSModel

        _model = Qwen3TTSModel.from_pretrained(
            MODEL_NAME,
            device_map=_device,
            dtype=torch.bfloat16 if _device == "cuda" else torch.float32,
        )
    return _model


def voice_file(voice_id: str) -> Path:
    # Ids are validated host-side, but this is a filesystem path in a server: check again.
    if not voice_id or "/" in voice_id or "\\" in voice_id or voice_id.startswith("."):
        raise HTTPException(status_code=400, detail=f"invalid voice id {voice_id!r}")
    return VOICE_DIR / f"{voice_id}.json"


@app.get("/health")
def health():
    return {
        "ok": True,
        "device": _device,
        "model": MODEL_NAME,
        "modelLoaded": _model is not None,
        "cuda": torch.cuda.is_available(),
        "gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
    }


class PrepareRequest(BaseModel):
    voiceId: str
    referenceWav: str
    referenceText: str | None = None


@app.post("/prepare")
def prepare(request: PrepareRequest):
    wav = Path(request.referenceWav)
    if not wav.is_file():
        raise HTTPException(status_code=404, detail=f"reference wav not found: {wav}")

    VOICE_DIR.mkdir(parents=True, exist_ok=True)
    target = voice_file(request.voiceId)
    target.write_text(json.dumps({
        "referenceWav": str(wav),
        "referenceText": request.referenceText or "",
    }), encoding="utf-8")
    return {"voiceId": request.voiceId, "cached": str(target)}


class SynthesizeRequest(BaseModel):
    voiceId: str
    text: str
    language: str
    temperature: float = 0.65
    speed: float = 1.0


@app.post("/synthesize")
def synthesize(request: SynthesizeRequest):
    cached = voice_file(request.voiceId)
    if not cached.is_file():
        raise HTTPException(status_code=409, detail=f"voice {request.voiceId!r} has not been prepared")
    voice = json.loads(cached.read_text(encoding="utf-8"))

    language = LANGUAGE_NAMES.get(request.language.lower(), request.language)

    with _lock:
        model = load()
        wavs, sample_rate = model.generate_voice_clone(
            text=request.text,
            language=language,
            ref_audio=voice["referenceWav"],
            ref_text=voice.get("referenceText") or "",
        )

    import numpy as np
    import soundfile

    buffer = io.BytesIO()
    soundfile.write(
        buffer, np.asarray(wavs[0], dtype="float32"), int(sample_rate), format="WAV", subtype="FLOAT"
    )
    return {
        "sampleRate": int(sample_rate),
        "wavBase64": base64.b64encode(buffer.getvalue()).decode("ascii"),
    }
