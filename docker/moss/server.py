"""HTTP front for MOSS-TTS, speaking the same three-endpoint contract as every container engine.

Unlike XTTS there is no separate conditioning step to cache: MOSS takes the reference audio on
every generation. So /prepare only records which recording belongs to a voice and checks it is
readable — cheap, but it keeps the host contract identical, which is the point of having one.
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

MODEL_NAME = os.environ.get("MOSS_MODEL", "OpenMOSS-Team/MOSS-TTS-Local-Transformer-v1.5")
VOICE_DIR = Path(os.environ.get("MOSS_VOICES", "/models/voices"))

app = FastAPI()

# One model, one lock. The host runs jobs serially, but a stray second request must queue
# rather than interleave with a generation in flight.
_lock = threading.Lock()
_model = None
_processor = None
_device = "cuda" if torch.cuda.is_available() else "cpu"
_dtype = torch.bfloat16 if torch.cuda.is_available() else torch.float32


def load():
    """Loaded on first use, not at import, so the container starts fast and /health answers."""
    global _model, _processor
    if _model is None:
        from transformers import AutoModel, AutoProcessor

        _processor = AutoProcessor.from_pretrained(MODEL_NAME, trust_remote_code=True)
        _processor.audio_tokenizer = _processor.audio_tokenizer.to(_device)
        _model = AutoModel.from_pretrained(
            MODEL_NAME,
            trust_remote_code=True,
            # sdpa rather than flash_attention_2: flash-attn is an optional build and its absence
            # should degrade speed, not break the engine.
            attn_implementation="sdpa",
            dtype=_dtype,
        ).to(_device)
        _model.eval()
    return _model, _processor


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
        "referenceText": request.referenceText,
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

    with _lock:
        model, processor = load()
        message = processor.build_user_message(
            text=request.text,
            reference=[voice["referenceWav"]],
            language=request.language,
        )
        batch = processor([[message]], mode="generation")
        with torch.no_grad():
            outputs = model.generate(
                input_ids=batch["input_ids"].to(_device),
                attention_mask=batch["attention_mask"].to(_device),
                max_new_tokens=4096,
                temperature=request.temperature,
            )
        decoded = processor.decode(outputs)
        audio = decoded[0].audio_codes_list[0].unsqueeze(0).cpu().to(torch.float32)
        sample_rate = int(processor.model_config.sampling_rate)

    import numpy as np
    import soundfile

    buffer = io.BytesIO()
    soundfile.write(
        buffer,
        np.asarray(audio.squeeze(0).numpy(), dtype="float32"),
        sample_rate,
        format="WAV",
        subtype="FLOAT",
    )
    return {
        "sampleRate": sample_rate,
        "wavBase64": base64.b64encode(buffer.getvalue()).decode("ascii"),
    }
