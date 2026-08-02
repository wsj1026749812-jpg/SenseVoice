import asyncio
import logging
import os
import re
import shutil
import tempfile
from contextlib import asynccontextmanager
from enum import Enum
from pathlib import Path
from typing import Annotated

import torch
from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from funasr import AutoModel
from funasr.utils.postprocess_utils import rich_transcription_postprocess


LOGGER = logging.getLogger("sensevoice")
TAG_PATTERN = re.compile(r"<\|.*?\|>")
SUPPORTED_SUFFIXES = {".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav"}


class Language(str, Enum):
    auto = "auto"
    zh = "zh"
    en = "en"
    yue = "yue"
    ja = "ja"
    ko = "ko"
    nospeech = "nospeech"


class ModelState:
    model: AutoModel | None = None
    device: str | None = None
    model_source: str | None = None
    vad_model_source: str | None = None
    semaphore: asyncio.Semaphore | None = None


state = ModelState()


def environment_int(name: str, default: int, minimum: int = 1) -> int:
    value = int(os.getenv(name, str(default)))
    if value < minimum:
        raise ValueError(f"{name} must be at least {minimum}")
    return value


def select_device() -> str:
    requested = os.getenv("SENSEVOICE_DEVICE", "auto").strip().lower()
    has_cuda = torch.cuda.is_available()

    if requested == "auto":
        return "cuda:0" if has_cuda else "cpu"
    if requested == "cpu":
        return "cpu"
    if requested.startswith("cuda"):
        if has_cuda:
            return requested
        if os.getenv("SENSEVOICE_ALLOW_CPU_FALLBACK", "false").lower() == "true":
            LOGGER.warning("CUDA was requested but is unavailable; using CPU fallback")
            return "cpu"
        raise RuntimeError(
            "SENSEVOICE_DEVICE requests CUDA, but CUDA is unavailable. "
            "Start the GPU mode with --gpus all on a Docker Desktop host with "
            "WSL 2/NVIDIA GPU support, or use SENSEVOICE_DEVICE=cpu."
        )
    raise ValueError("SENSEVOICE_DEVICE must be auto, cpu, or a cuda device such as cuda:0")


def local_or_hub_model(local_environment: str, model_environment: str, default: str) -> str:
    local_path = os.getenv(local_environment)
    if local_path:
        local_model = Path(local_path)
        if (local_model / ".model-ready").is_file():
            return str(local_model)
    return os.getenv(model_environment, default)


def load_model() -> None:
    device = select_device()
    source = local_or_hub_model(
        "SENSEVOICE_MODEL", "SENSEVOICE_MODEL_ID", "iic/SenseVoiceSmall"
    )
    vad_source = local_or_hub_model(
        "SENSEVOICE_VAD_MODEL",
        "SENSEVOICE_VAD_MODEL_ID",
        "iic/speech_fsmn_vad_zh-cn-16k-common-pytorch",
    )
    LOGGER.info("Loading SenseVoice model from %s and VAD from %s on %s", source, vad_source, device)
    state.model = AutoModel(
        model=source,
        vad_model=vad_source,
        vad_kwargs={"max_single_segment_time": 30000},
        device=device,
        disable_update=True,
    )
    state.device = device
    state.model_source = source
    state.vad_model_source = vad_source
    state.semaphore = asyncio.Semaphore(environment_int("MAX_CONCURRENT_REQUESTS", 1))
    LOGGER.info("SenseVoice is ready on %s", device)


@asynccontextmanager
async def lifespan(_: FastAPI):
    load_model()
    yield
    state.model = None


app = FastAPI(
    title="SenseVoice API",
    version="1.0.0",
    description="Offline-capable SenseVoiceSmall speech understanding service.",
    lifespan=lifespan,
)


def normalize_results(result: object) -> list[dict]:
    if isinstance(result, list) and len(result) == 1 and isinstance(result[0], list):
        result = result[0]
    if not isinstance(result, list) or not all(isinstance(item, dict) for item in result):
        raise RuntimeError("Unexpected result shape returned by FunASR")
    return result


def present_result(item: dict, filename: str) -> dict:
    raw_text = str(item.get("text", ""))
    output = dict(item)
    output["filename"] = filename
    output["raw_text"] = raw_text
    output["clean_text"] = TAG_PATTERN.sub("", raw_text)
    output["text"] = rich_transcription_postprocess(raw_text)
    return output


@app.get("/")
def root() -> dict:
    return {"service": "sensevoice", "docs": "/docs", "health": "/health"}


@app.get("/health")
def health() -> dict:
    if state.model is None:
        raise HTTPException(status_code=503, detail="Model is not ready")
    return {
        "status": "ok",
        "device": state.device,
        "model_source": state.model_source,
        "vad_model_source": state.vad_model_source,
        "cuda_available": torch.cuda.is_available(),
    }


@app.post("/api/v1/asr")
async def transcribe(
    files: Annotated[list[UploadFile], File(description="Audio files")],
    lang: Annotated[Language, Form(description="Audio language")] = Language.auto,
    use_itn: Annotated[bool, Form(description="Apply inverse text normalization")] = True,
) -> dict:
    if state.model is None or state.semaphore is None:
        raise HTTPException(status_code=503, detail="Model is not ready")
    if not files:
        raise HTTPException(status_code=422, detail="At least one audio file is required")

    temp_dir = Path(tempfile.mkdtemp(prefix="sensevoice-"))
    paths: list[str] = []
    filenames: list[str] = []
    try:
        for index, upload in enumerate(files):
            original_name = upload.filename or f"audio-{index}.wav"
            suffix = Path(original_name).suffix.lower()
            if suffix not in SUPPORTED_SUFFIXES:
                raise HTTPException(
                    status_code=415,
                    detail=f"Unsupported audio format: {suffix or 'unknown'}",
                )
            destination = temp_dir / f"audio-{index}{suffix}"
            with destination.open("wb") as target:
                shutil.copyfileobj(upload.file, target)
            paths.append(str(destination))
            filenames.append(original_name)

        batch_size_s = environment_int("BATCH_SIZE_S", 60)
        async with state.semaphore:
            generated = await asyncio.to_thread(
                state.model.generate,
                input=paths,
                cache={},
                language=lang.value,
                use_itn=use_itn,
                batch_size_s=batch_size_s,
            )
        results = normalize_results(generated)
        return {
            "result": [
                present_result(item, filenames[index] if index < len(filenames) else "audio")
                for index, item in enumerate(results)
            ]
        }
    finally:
        for upload in files:
            await upload.close()
        shutil.rmtree(temp_dir, ignore_errors=True)
