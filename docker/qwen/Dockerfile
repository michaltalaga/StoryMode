# Qwen3-TTS, Polish fine-tune, behind the standard container contract.
#
# The strongest candidate of the lot on paper: Apache 2.0, clones from a recording, and only
# 1.7B — small enough to load fast and leave room beside whisper. The base model does not speak
# Polish; this is a community fine-tune that adds it, which is a different and usually better
# proposition than a multilingual model that merely lists the language.
#
# Longer term this is also the only candidate with a C#/ONNX export path (elbruno/ElBruno.QwenTTS),
# so if it wins on quality it could eventually run in-process and drop the container entirely.

FROM pytorch/pytorch:2.5.1-cuda12.4-cudnn9-runtime

RUN apt-get update && apt-get install -y --no-install-recommends \
        libsndfile1 ffmpeg \
    && rm -rf /var/lib/apt/lists/*

# Published on PyPI, unlike most of this field — no git clone, no editable install.
RUN pip install --no-cache-dir "qwen-tts" "fastapi==0.115.6" "uvicorn[standard]==0.34.0" soundfile

# Weights land in /models, a volume, so rebuilding the image re-downloads nothing.
ENV HF_HOME=/models/hf
ENV QWEN_MODEL=agnostic/Qwen3-TTS-Polish

WORKDIR /app
COPY server.py /app/server.py

EXPOSE 8020
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8020"]
