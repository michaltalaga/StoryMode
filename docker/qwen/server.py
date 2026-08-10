"""HTTP front for Qwen3-TTS (Polish fine-tune), speaking the standard container contract.

Two things about this model are not what its repository name suggests, both discovered by asking
the installed package rather than reading the card:

  * It is a **CustomVoice** checkpoint, so it does not clone. `generate_voice_clone` raises, and
    the speaker-encoder weights are discarded at load. It ships one trained speaker,
    `polish_speaker`, and that is the whole voice list.
  * The library's language whitelist has no Polish in it, despite the fine-tune being Polish.
    `language="auto"` is the way in — the model is Polish-trained, the tag just cannot say so.

So /prepare records which built-in speaker a voice uses and nothing else, and /synthesize calls
generate_custom_voice. The endpoints stay the same shape as every other engine's, which is what
lets the host treat them identically.
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
DEFAULT_SPEAKER = os.environ.get("QWEN_SPEAKER", "polish_speaker")

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
        "clones": False,
        "speakers": [DEFAULT_SPEAKER],
        "modelLoaded": _model is not None,
        "cuda": torch.cuda.is_available(),
        "gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
    }


class PrepareRequest(BaseModel):
    voiceId: str
    speaker: str | None = None
    referenceWav: str | None = None
    referenceText: str | None = None


@app.post("/prepare")
def prepare(request: PrepareRequest):
    """Records the built-in speaker for this voice. Any reference audio is ignored — saying so
    here rather than silently dropping it is the difference between a limitation and a lie."""
    VOICE_DIR.mkdir(parents=True, exist_ok=True)
    target = voice_file(request.voiceId)
    target.write_text(json.dumps({"speaker": request.speaker or DEFAULT_SPEAKER}), encoding="utf-8")
    return {"voiceId": request.voiceId, "speaker": request.speaker or DEFAULT_SPEAKER, "clones": False}


class SynthesizeRequest(BaseModel):
    voiceId: str
    text: str
    language: str
    speaker: str | None = None
    temperature: float = 0.65
    speed: float = 1.0


@app.post("/synthesize")
def synthesize(request: SynthesizeRequest):
    speaker = request.speaker
    if not speaker:
        cached = voice_file(request.voiceId)
        speaker = (
            json.loads(cached.read_text(encoding="utf-8")).get("speaker")
            if cached.is_file()
            else DEFAULT_SPEAKER
        )

    with _lock:
        model = load()
        # "auto" rather than the requested tag: the whitelist has no Polish even though this
        # checkpoint is Polish-trained, and passing an unsupported tag raises.
        wavs, sample_rate = model.generate_custom_voice(
            text=request.text,
            speaker=speaker,
            language="auto",
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
