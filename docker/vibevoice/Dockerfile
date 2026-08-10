# VibeVoice behind the same HTTP contract as the other container engines.
#
# Microsoft withdrew the TTS inference code in September 2025 after finding it misused, and
# restored the repository without it. What remains official is the Realtime-0.5B model and the ASR
# work; the long-form 1.5B and 7B weights survive on the Hub, and the inference code survives as a
# community fork under MIT. That fork is what this image installs.
#
# Two models, because VibeVoice has no single one that does both jobs:
#   VIBEVOICE_MODE=clone   vibevoice/VibeVoice-1.5B — clones a recording, English, up to ~90
#                          minutes in a single pass, which is the property nothing else here has.
#   VIBEVOICE_MODE=polish  microsoft/VibeVoice-Realtime-0.5B plus two Polish voices fine-tuned on
#                          it. Fixed voices: the streaming model has no speaker encoder to clone
#                          with, and the base model does not speak Polish at all.

FROM nvidia/cuda:12.8.1-cudnn-runtime-ubuntu24.04

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3.12 python3.12-venv python3-pip git ca-certificates ffmpeg libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

# Ubuntu 24.04 marks the system python as externally managed; a venv is the sanctioned way in.
RUN python3.12 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

RUN pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cu128 \
        torch torchaudio \
    && git clone --depth 1 https://github.com/vibevoice-community/VibeVoice.git /opt/vibevoice \
    && pip install --no-cache-dir -e /opt/vibevoice \
    && pip install --no-cache-dir "fastapi==0.115.6" "uvicorn[standard]==0.34.0" soundfile huggingface_hub

# Weights land in /models, which is a volume — rebuilding the image re-downloads nothing.
ENV HF_HOME=/models/hf
ENV VIBEVOICE_MODE=clone
ENV VIBEVOICE_CLONE_MODEL=vibevoice/VibeVoice-1.5B
ENV VIBEVOICE_POLISH_MODEL=microsoft/VibeVoice-Realtime-0.5B
ENV VIBEVOICE_POLISH_VOICES=Sticzu/vibevoice-polish-voices

WORKDIR /app
COPY server.py /app/server.py

EXPOSE 8020
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8020"]
