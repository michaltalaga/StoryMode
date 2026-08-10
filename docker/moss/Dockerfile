# MOSS-TTS behind the same HTTP contract as the other container engines.
#
# The one that matters licensing-wise: Apache 2.0, where XTTS and Higgs are both non-commercial.
# If its Polish holds up, this is the engine that can stay when the repo goes public.
#
# Default checkpoint is the 4B Local-Transformer rather than the 8B Delay: the 8B would leave
# little room beside whisper and whatever else is resident on a 16 GB card, and jobs here are
# serial anyway so throughput is not the constraint.

FROM nvidia/cuda:12.8.1-cudnn-runtime-ubuntu24.04

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3.12 python3.12-venv python3-pip git ffmpeg libsndfile1 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Ubuntu 24.04 marks the system python as externally managed; a venv is the sanctioned way in.
RUN python3.12 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

# Installed from source because that is the only distribution the project documents.
RUN git clone --depth 1 https://github.com/OpenMOSS/MOSS-TTS.git /opt/moss \
    && pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cu128 \
        -e "/opt/moss[torch-runtime]" \
    && pip install --no-cache-dir "fastapi==0.115.6" "uvicorn[standard]==0.34.0" soundfile

# Weights land in /models, which is a volume — rebuilding the image re-downloads nothing.
ENV HF_HOME=/models/hf
ENV MOSS_MODEL=OpenMOSS-Team/MOSS-TTS-Local-Transformer-v1.5

WORKDIR /app
COPY server.py /app/server.py

EXPOSE 8020
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8020"]
