# A CUDA-enabled PyTorch image also runs on CPU when no NVIDIA runtime is passed
# to the container. That lets one image cover both supported deployment modes.
FROM pytorch/pytorch:2.5.1-cuda12.4-cudnn9-runtime

ARG MODEL_ID=iic/SenseVoiceSmall
ARG VAD_MODEL_ID=iic/speech_fsmn_vad_zh-cn-16k-common-pytorch
ARG PRELOAD_MODEL=true

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    SENSEVOICE_DEVICE=auto \
    SENSEVOICE_MODEL=/models/sensevoice-small \
    SENSEVOICE_MODEL_ID=iic/SenseVoiceSmall \
    SENSEVOICE_VAD_MODEL=/models/fsmn-vad \
    SENSEVOICE_VAD_MODEL_ID=iic/speech_fsmn_vad_zh-cn-16k-common-pytorch \
    MODEL_CACHE=/models/cache \
    MODELSCOPE_CACHE=/models/cache \
    HF_HOME=/models/huggingface \
    TMPDIR=/tmp

WORKDIR /app

RUN sed -i \
        -e 's|http://archive.ubuntu.com/ubuntu/|http://mirrors.aliyun.com/ubuntu/|g' \
        -e 's|http://security.ubuntu.com/ubuntu/|http://mirrors.aliyun.com/ubuntu/|g' \
        /etc/apt/sources.list \
    && for attempt in 1 2 3; do \
        rm -rf /var/lib/apt/lists/* /var/cache/apt/archives/*; \
        if apt-get update && apt-get install -y --no-install-recommends ffmpeg libsndfile1 ca-certificates; then break; fi; \
        if [ "${attempt}" = "3" ]; then exit 1; fi; \
        sleep 5; \
    done \
    && rm -rf /var/lib/apt/lists/* /var/cache/apt/archives/*

COPY requirements.txt /app/requirements.txt
RUN python -m pip install --upgrade pip \
    && python -m pip install -r /app/requirements.txt

COPY app /app/app
COPY docker/entrypoint.sh /usr/local/bin/sensevoice-entrypoint
RUN chmod 755 /usr/local/bin/sensevoice-entrypoint \
    && mkdir -p /models /opt/sensevoice-model

# The resulting image contains the model, so `docker save` produces a portable,
# offline artifact. Set PRELOAD_MODEL=false only for development builds.
RUN if [ "${PRELOAD_MODEL}" = "true" ]; then \
      python -c "from modelscope import snapshot_download; snapshot_download('${MODEL_ID}', local_dir='/opt/sensevoice-model')" \
      && touch /opt/sensevoice-model/.model-ready \
      && python -c "from modelscope import snapshot_download; snapshot_download('${VAD_MODEL_ID}', local_dir='/opt/fsmn-vad')" \
      && touch /opt/fsmn-vad/.model-ready; \
    fi

EXPOSE 50000

HEALTHCHECK --interval=30s --timeout=5s --start-period=180s --retries=3 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:50000/health', timeout=3).read()" || exit 1

ENTRYPOINT ["/usr/local/bin/sensevoice-entrypoint"]
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "50000"]
