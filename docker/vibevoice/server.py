"""HTTP front for VibeVoice, speaking the same three-endpoint contract as every container engine.

One image, two models, chosen by VIBEVOICE_MODE — see the Dockerfile for why. The two share
nothing but this file: different classes, different processors, different notions of what a voice
even is. Keeping them behind one contract is the whole point of the contract.
"""

import base64
import copy
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

MODE = os.environ.get("VIBEVOICE_MODE", "clone").strip().lower()
CLONE_MODEL = os.environ.get("VIBEVOICE_CLONE_MODEL", "vibevoice/VibeVoice-1.5B")
POLISH_MODEL = os.environ.get("VIBEVOICE_POLISH_MODEL", "microsoft/VibeVoice-Realtime-0.5B")
POLISH_VOICES_REPO = os.environ.get("VIBEVOICE_POLISH_VOICES", "Sticzu/vibevoice-polish-voices")
VOICE_DIR = Path(os.environ.get("VIBEVOICE_VOICES", "/models/voices"))

# The Polish voices are cached prompts, not weights: each one is a prefilled state captured from
# the streaming model after conditioning on a fine-tuned speaker. Names are what the host passes
# as `speaker`; a voice that is not in here does not exist.
POLISH_SPEAKERS = {
    "agnes": "pl-Agnes_woman.pt",
    "marek": "pl-Marek_man.pt",
}

SAMPLE_RATE = 24_000

app = FastAPI()

# One model, one lock. The host runs jobs serially, but a stray second request must queue rather
# than interleave with a generation in flight.
_lock = threading.Lock()
_model = None
_processor = None
_voices: dict[str, object] = {}
_device = "cuda" if torch.cuda.is_available() else "cpu"
_dtype = torch.bfloat16 if torch.cuda.is_available() else torch.float32


def load():
    """Loaded on first use, not at import, so the container starts fast and /health answers."""
    global _model, _processor
    if _model is not None:
        return _model, _processor

    if MODE == "polish":
        from vibevoice.modular.modeling_vibevoice_streaming_inference import (
            VibeVoiceStreamingForConditionalGenerationInference,
        )
        from vibevoice.processor.vibevoice_streaming_processor import VibeVoiceStreamingProcessor

        model = VibeVoiceStreamingForConditionalGenerationInference.from_pretrained(
            # sdpa rather than flash_attention_2: flash-attn is an optional build and its absence
            # should cost speed, not break the engine.
            POLISH_MODEL, torch_dtype=_dtype, attn_implementation="sdpa",
        ).to(_device)
        model.eval()
        # Six diffusion steps is the setting the voices were published with. More steps buy some
        # quality for a proportional amount of time, and this engine is not the fast one.
        model.set_ddpm_inference_steps(6)
        scheduler = model.model.noise_scheduler
        scheduler.set_timesteps(6)
        # The scheduler builds its timesteps on the CPU; generation reads them on the GPU.
        scheduler.timesteps = scheduler.timesteps.to(_device)
        if hasattr(scheduler, "sigmas"):
            scheduler.sigmas = scheduler.sigmas.to(_device)
        _processor = VibeVoiceStreamingProcessor.from_pretrained(POLISH_MODEL)
    else:
        from vibevoice.modular.modeling_vibevoice_inference import (
            VibeVoiceForConditionalGenerationInference,
        )
        from vibevoice.processor.vibevoice_processor import VibeVoiceProcessor

        model = VibeVoiceForConditionalGenerationInference.from_pretrained(
            CLONE_MODEL, torch_dtype=_dtype, device_map=_device, attn_implementation="sdpa",
        )
        model.eval()
        model.set_ddpm_inference_steps(num_steps=10)
        _processor = VibeVoiceProcessor.from_pretrained(CLONE_MODEL)

    _model = model
    return _model, _processor


def polish_voice(speaker: str):
    """The cached prompt for a Polish speaker, downloaded once into the models volume.

    `torch.load(weights_only=False)` unpickles, which is code execution — unavoidable here, because
    these files hold live cache objects rather than plain tensors. It happens inside the container,
    from one pinned repository, on a machine that already trusts the model weights next to it.
    """
    if speaker not in _voices:
        from huggingface_hub import hf_hub_download

        path = hf_hub_download(repo_id=POLISH_VOICES_REPO, filename=POLISH_SPEAKERS[speaker])
        _voices[speaker] = _to_device(torch.load(path, map_location=_device, weights_only=False))
    return _voices[speaker]


def _to_device(obj):
    """Walks whatever the cached prompt turns out to be, moving tensors to the GPU as it goes."""
    if isinstance(obj, torch.Tensor):
        return obj.to(_device).to(_dtype) if obj.is_floating_point() else obj.to(_device)
    if isinstance(obj, dict):
        return {key: _to_device(value) for key, value in obj.items()}
    if isinstance(obj, list):
        return [_to_device(item) for item in obj]
    if isinstance(obj, tuple):
        return tuple(_to_device(item) for item in obj)
    if hasattr(obj, "key_cache") and hasattr(obj, "value_cache"):
        obj.key_cache = [_to_device(t) for t in obj.key_cache]
        obj.value_cache = [_to_device(t) for t in obj.value_cache]
    return obj


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
        "mode": MODE,
        "model": POLISH_MODEL if MODE == "polish" else CLONE_MODEL,
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
    # In polish mode the voices are built in, so there is nothing a recording could teach. The
    # host already knows this and skips the call; answering plainly is cheaper than a 400 nobody
    # is going to read.
    if MODE == "polish":
        return {"voiceId": request.voiceId, "cached": None}

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
    speaker: str | None = None


@app.post("/synthesize")
def synthesize(request: SynthesizeRequest):
    # Guidance strength, not sampling temperature: VibeVoice generates greedily and takes its
    # character from how hard classifier-free guidance pushes it toward the conditioning. Mapped
    # from the host's delivery presets, whose 0.5/0.65/0.85 span this range readably.
    cfg_scale = round(1.5 + (max(0.1, min(request.temperature, 1.5)) - 0.5) * 2.0, 2)

    with _lock:
        model, processor = load()
        if MODE == "polish":
            audio = _generate_polish(model, processor, request, cfg_scale)
        else:
            audio = _generate_clone(model, processor, request, cfg_scale)

    buffer = io.BytesIO()
    soundfile.write(buffer, np.asarray(audio, dtype="float32"), SAMPLE_RATE,
                    format="WAV", subtype="FLOAT")
    return {
        "sampleRate": SAMPLE_RATE,
        "wavBase64": base64.b64encode(buffer.getvalue()).decode("ascii"),
    }


def _generate_polish(model, processor, request: SynthesizeRequest, cfg_scale: float):
    speaker = (request.speaker or "agnes").strip().lower()
    if speaker not in POLISH_SPEAKERS:
        raise HTTPException(
            status_code=400,
            detail=f"unknown speaker {speaker!r}; this model has {sorted(POLISH_SPEAKERS)}")

    # deepcopy per call: generate() consumes the cached prompt, and a voice is used many times
    # over one story.
    cached = copy.deepcopy(polish_voice(speaker))
    inputs = processor.process_input_with_cached_prompt(
        text=request.text,
        cached_prompt=cached,
        padding=True,
        return_tensors="pt",
        return_attention_mask=True,
    )
    for key, value in inputs.items():
        if torch.is_tensor(value):
            inputs[key] = value.to(_device)

    with torch.inference_mode():
        outputs = model.generate(
            **inputs,
            max_new_tokens=None,
            cfg_scale=cfg_scale,
            tokenizer=processor.tokenizer,
            generation_config={"do_sample": False},
            verbose=False,
            all_prefilled_outputs=copy.deepcopy(polish_voice(speaker)),
        )
    return _waveform(outputs)


def _generate_clone(model, processor, request: SynthesizeRequest, cfg_scale: float):
    cached = voice_file(request.voiceId)
    if not cached.is_file():
        raise HTTPException(status_code=409, detail=f"voice {request.voiceId!r} has not been prepared")
    voice = json.loads(cached.read_text(encoding="utf-8"))

    # VibeVoice reads a transcript, not a sentence: every line is attributed to a numbered
    # speaker. One narrator, so one speaker, but the prefix is not optional — without it the
    # processor finds no segments and generates nothing.
    script = "Speaker 1: " + request.text.replace("’", "'").replace("\n", " ").strip()
    inputs = processor(
        text=[script],
        voice_samples=[[voice["referenceWav"]]],
        padding=True,
        return_tensors="pt",
        return_attention_mask=True,
    )
    for key, value in inputs.items():
        if torch.is_tensor(value):
            inputs[key] = value.to(_device)

    with torch.inference_mode():
        outputs = model.generate(
            **inputs,
            max_new_tokens=None,
            cfg_scale=cfg_scale,
            tokenizer=processor.tokenizer,
            generation_config={"do_sample": False},
            verbose=False,
        )
    return _waveform(outputs)


def _waveform(outputs):
    speech = getattr(outputs, "speech_outputs", None)
    if not speech or speech[0] is None:
        raise HTTPException(status_code=502, detail="VibeVoice generated no audio for this text.")
    return speech[0].detach().cpu().to(torch.float32).reshape(-1).numpy()
