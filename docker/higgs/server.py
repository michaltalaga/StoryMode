"""HTTP front for Higgs TTS 3, speaking the same three-endpoint contract as every container engine.

Like MOSS, there is no separate conditioning pass to cache: the reference clip is fed to every
generation. So /prepare only records which recording belongs to a voice and decodes it once —
cheap, but it keeps the host contract identical, which is the point of having one.

There is no language parameter. Higgs infers the language from the text it is given, and its
inline control tokens travel in the text as well, so the host's language field is only used on the
host, to refuse a language this engine does not claim.
"""

import base64
import io
import json
import os
import threading
from pathlib import Path

import numpy as np
import soundfile
import torch
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

MODEL_NAME = os.environ.get("HIGGS_MODEL", "multimodalart/higgs-audio-v3-tts-4b-transformers")
VOICE_DIR = Path(os.environ.get("HIGGS_VOICES", "/models/voices"))

app = FastAPI()

# One model, one lock. The host runs jobs serially, but a stray second request must queue rather
# than interleave with a generation in flight.
_lock = threading.Lock()
_model = None
_tokenizer = None
_references: dict[str, tuple] = {}
_device = "cuda" if torch.cuda.is_available() else "cpu"
_dtype = torch.bfloat16 if torch.cuda.is_available() else torch.float32


def load():
    """Loaded on first use, not at import, so the container starts fast and /health answers."""
    global _model, _tokenizer
    if _model is None:
        from transformers import AutoModelForCausalLM, AutoTokenizer

        _tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME)
        _model = AutoModelForCausalLM.from_pretrained(
            MODEL_NAME, trust_remote_code=True, dtype=_dtype,
        ).to(_device)
        _model.eval()
    return _model, _tokenizer


def voice_file(voice_id: str) -> Path:
    # Ids are validated host-side, but this is a filesystem path in a server: check again.
    if not voice_id or "/" in voice_id or "\\" in voice_id or voice_id.startswith("."):
        raise HTTPException(status_code=400, detail=f"invalid voice id {voice_id!r}")
    return VOICE_DIR / f"{voice_id}.json"


def reference(voice_id: str, path: str) -> tuple:
    """The reference clip as [channels, samples] float32 plus its rate, read once and kept."""
    if voice_id not in _references:
        data, sample_rate = soundfile.read(path, dtype="float32", always_2d=True)
        _references[voice_id] = (torch.from_numpy(np.ascontiguousarray(data.T)), sample_rate)
    return _references[voice_id]


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
        "referenceText": request.referenceText,
    }), encoding="utf-8")
    # Anything unreadable should fail here, while someone is watching an install, rather than
    # halfway through a story render an hour later.
    _references.pop(request.voiceId, None)
    reference(request.voiceId, str(wav))
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

    with _lock:
        model, tokenizer = load()
        audio, sample_rate = reference(request.voiceId, voice["referenceWav"])
        with torch.inference_mode():
            wav = model.generate_speech(
                request.text,
                tokenizer,
                reference_audio=audio,
                reference_sample_rate=sample_rate,
                # Higgs clones markedly better when told what the reference says; absent is fine.
                reference_text=voice.get("referenceText"),
                temperature=request.temperature,
                top_p=0.95,
            )
        out = wav.detach().cpu().to(torch.float32).reshape(-1).numpy()
        rate = int(model.config.sample_rate)

    buffer = io.BytesIO()
    soundfile.write(buffer, np.asarray(out, dtype="float32"), rate, format="WAV", subtype="FLOAT")
    return {
        "sampleRate": rate,
        "wavBase64": base64.b64encode(buffer.getvalue()).decode("ascii"),
    }
