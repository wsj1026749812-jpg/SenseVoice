#!/usr/bin/env bash
set -euo pipefail

model_dir="${SENSEVOICE_MODEL:-/models/sensevoice-small}"
seed_dir="/opt/sensevoice-model"
vad_model_dir="${SENSEVOICE_VAD_MODEL:-/models/fsmn-vad}"
vad_seed_dir="/opt/fsmn-vad"

# A named or bind-mounted /models directory hides files baked into the image.
# Seed it once so the persistent runtime model remains available after restarts.
if [[ -f "${seed_dir}/.model-ready" && ! -f "${model_dir}/.model-ready" ]]; then
    echo "Seeding bundled SenseVoice model into ${model_dir}..."
    mkdir -p "${model_dir}"
    cp -a "${seed_dir}/." "${model_dir}/"
fi

if [[ -f "${vad_seed_dir}/.model-ready" && ! -f "${vad_model_dir}/.model-ready" ]]; then
    echo "Seeding bundled FSMN-VAD model into ${vad_model_dir}..."
    mkdir -p "${vad_model_dir}"
    cp -a "${vad_seed_dir}/." "${vad_model_dir}/"
fi

exec "$@"
