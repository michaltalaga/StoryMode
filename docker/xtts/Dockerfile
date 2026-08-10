# XTTS-v2 behind a tiny HTTP server.
#
# Why a container: XTTS has no ONNX export worth trusting, so the alternatives were another
# hand-written port (the Chatterbox one took weeks and a token-parity harness) or Python. Python
# is not welcome on the host, so it lives in here — the host talks HTTP and never sees it.
#
# The model is NOT baked into the image. It downloads on first run into /models, which is a
# volume, so rebuilding the image does not re-download 2 GB and the weights stay inspectable
# on the host like everything else in this project.

FROM pytorch/pytorch:2.5.1-cuda12.4-cudnn9-runtime

# libsndfile: soundfile's native backend. ffmpeg: torchaudio's decoder for arbitrary reference wavs.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libsndfile1 ffmpeg \
    && rm -rf /var/lib/apt/lists/*

# coqui-tts is the maintained fork; the original `TTS` package died with Coqui in 2024 and
# pins a torch too old for this base image.
RUN pip install --no-cache-dir "coqui-tts==0.25.1" "fastapi==0.115.6" "uvicorn[standard]==0.34.0"

# Where the weights land. Mounted as a volume from <repo>/models/xtts.
ENV TTS_HOME=/models
ENV HF_HOME=/models/hf

WORKDIR /app
COPY server.py /app/server.py

EXPOSE 8020
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8020"]
