"""Small Piper process wrapper used by the self-contained Windows service."""

from __future__ import annotations

import argparse
import sys
import wave

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
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    text = sys.stdin.read().strip()
    if not text:
        raise ValueError("Text input is empty.")
    if args.stream == (args.output is not None):
        raise ValueError("Use --output for WAV synthesis or --stream for raw PCM output.")

    voice = PiperVoice.load(args.model, config_path=args.config, use_cuda=False)
    config = SynthesisConfig(
        length_scale=args.length_scale,
        noise_scale=args.noise_scale,
        noise_w_scale=args.noise_w_scale,
    )
    if args.stream:
        for chunk in voice.synthesize(text, syn_config=config):
            sys.stdout.buffer.write(chunk.audio_int16_bytes)
            sys.stdout.buffer.flush()
        return 0

    with wave.open(args.output, "wb") as wav_file:
        voice.synthesize_wav(text, wav_file, syn_config=config)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:  # The .NET API surfaces this as a validation error.
        print(f"Piper synthesis failed: {error}", file=sys.stderr)
        raise SystemExit(1)
