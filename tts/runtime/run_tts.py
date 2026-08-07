"""Piper wrapper with one-shot and persistent-worker modes."""

from __future__ import annotations

import argparse
import json
import struct
import sys
import wave
from pathlib import Path

from piper import PiperVoice, SynthesisConfig


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--output")
    parser.add_argument("--length-scale", type=float, default=1.0)
    parser.add_argument("--noise-scale", type=float, default=0.667)
    parser.add_argument("--noise-w-scale", type=float, default=0.8)
    parser.add_argument("--stream", action="store_true")
    parser.add_argument("--worker", action="store_true")
    return parser.parse_args()


def synthesis_config(values: dict | argparse.Namespace) -> SynthesisConfig:
    if isinstance(values, dict):
        return SynthesisConfig(
            length_scale=float(values.get("length_scale", 1.0)),
            noise_scale=float(values.get("noise_scale", 0.667)),
            noise_w_scale=float(values.get("noise_w_scale", 0.8)),
        )
    return SynthesisConfig(
        length_scale=values.length_scale,
        noise_scale=values.noise_scale,
        noise_w_scale=values.noise_w_scale,
    )


def write_json(value: dict) -> None:
    sys.stdout.buffer.write(json.dumps(value, ensure_ascii=False).encode("utf-8") + b"\n")
    sys.stdout.buffer.flush()


def synthesize_wav(voice: PiperVoice, text: str, output: str, config: SynthesisConfig) -> None:
    output_path = Path(output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(output_path), "wb") as wav_file:
        voice.synthesize_wav(text, wav_file, syn_config=config)


def synthesize_stream(voice: PiperVoice, text: str, config: SynthesisConfig) -> None:
    for chunk in voice.synthesize(text, syn_config=config):
        audio = chunk.audio_int16_bytes
        sys.stdout.buffer.write(struct.pack("<I", len(audio)))
        sys.stdout.buffer.write(audio)
        sys.stdout.buffer.flush()


def worker_main(args: argparse.Namespace) -> int:
    voice = PiperVoice.load(args.model, config_path=args.config, use_cuda=False)
    write_json({"status": "ready", "sample_rate": voice.config.sample_rate})

    for raw_line in sys.stdin.buffer:
        mode = "unknown"
        try:
            # .NET may prefix the first StandardInput write with a UTF-8 BOM.
            # utf-8-sig accepts that first command and behaves like utf-8 otherwise.
            command = json.loads(raw_line.decode("utf-8-sig"))
            mode = str(command.get("mode", ""))
            text = str(command.get("text", "")).strip()
            if not text:
                raise ValueError("Text input is empty.")
            config = synthesis_config(command)
            if mode == "wav":
                output = str(command.get("output", ""))
                if not output:
                    raise ValueError("Worker WAV request is missing output.")
                synthesize_wav(voice, text, output, config)
                write_json({"status": "ok"})
            elif mode == "stream":
                synthesize_stream(voice, text, config)
                sys.stdout.buffer.write(struct.pack("<I", 0))
                write_json({"status": "ok"})
            else:
                raise ValueError(f"Unsupported worker mode: {mode}")
        except Exception as error:
            if mode == "stream":
                sys.stdout.buffer.write(struct.pack("<I", 0))
            write_json({"status": "error", "detail": f"Piper synthesis failed: {error}"})
    return 0


def one_shot_main(args: argparse.Namespace) -> int:
    text = sys.stdin.read().strip()
    if not text:
        raise ValueError("Text input is empty.")
    if args.stream == (args.output is not None):
        raise ValueError("Use --output for WAV synthesis or --stream for raw PCM output.")

    voice = PiperVoice.load(args.model, config_path=args.config, use_cuda=False)
    config = synthesis_config(args)
    if args.stream:
        for chunk in voice.synthesize(text, syn_config=config):
            sys.stdout.buffer.write(chunk.audio_int16_bytes)
            sys.stdout.buffer.flush()
    else:
        synthesize_wav(voice, text, args.output, config)
    return 0


def main() -> int:
    args = parse_args()
    return worker_main(args) if args.worker else one_shot_main(args)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"Piper synthesis failed: {error}", file=sys.stderr)
        raise SystemExit(1)
