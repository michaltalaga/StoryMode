# Higgs TTS 3 behind the same HTTP contract as the other container engines.
#
# The best claimed Polish of anything surveyed: single-digit WER/CER on 102 languages with Polish
# in the polished tier, plus zero-shot cloning. The licence is the cost — research and
# non-commercial, with a Creator Use Grant that covers monetised podcasts and videos as long as
# Boson AI's Higgs Audio is credited.
#
# Boson serve it with SGLang-Omni, which is not an option here: sglang-omni depends on
# flash-attn-4 and CUDA 13 wheels, and flash attention 4 needs Hopper or newer. This machine has
# an Ada card. What makes it work anyway is a trust_remote_code repackaging of the same weights —
# a standard Qwen3-4B backbone with a fused multi-codebook audio head — that loads on plain
# transformers, exactly like MOSS does. Same checkpoint, no datacentre GPU required.

FROM nvidia/cuda:12.8.1-cudnn-runtime-ubuntu24.04

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3.12 python3.12-venv python3-pip ca-certificates libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

# Ubuntu 24.04 marks the system python as externally managed; a venv is the sanctioned way in.
RUN python3.12 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

# transformers 5.5 or newer: the remote code uses APIs that do not exist before it.
RUN pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cu128 \
        torch torchaudio \
    && pip install --no-cache-dir "transformers>=5.5" accelerate \
        "fastapi==0.115.6" "uvicorn[standard]==0.34.0" soundfile

# Weights land in /models, which is a volume — rebuilding the image re-downloads nothing.
ENV HF_HOME=/models/hf
ENV HIGGS_MODEL=multimodalart/higgs-audio-v3-tts-4b-transformers

WORKDIR /app
COPY server.py /app/server.py

EXPOSE 8020
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8020"]
