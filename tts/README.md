# Piper TTS Lite for Windows CPU

This package runs the official Piper engine with the official Chinese
`zh_CN-huayan-medium` ONNX voice locally on Windows x64. The archive bundles a
portable Python runtime, ONNX Runtime, Piper, the voice model, and a
self-contained .NET HTTP service. The deployment computer needs no Docker,
WSL, GPU, Python, or internet connection.

## Start

Extract `PiperTtsLite-windows-x64.zip`, then double-click
`Start-PiperTtsLite.cmd`. It starts a local-only service at
`http://127.0.0.1:50100`. Open that address in Edge or Chrome for the bundled
text-to-speech, streaming playback, batch test, and report-export page.

To use another port or allow LAN clients:

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-PiperTtsLite.ps1 -Port 50100
```

Use `-BindAddress 0.0.0.0` only when another LAN machine needs to call the
service. `Stop-PiperTtsLite.ps1` stops the background service.

## Local HTTP API

Check service status:

```powershell
Invoke-RestMethod http://127.0.0.1:50100/health
```

Generate a complete WAV file:

```powershell
$body = @{ text = "您好，这是本地中文语音合成测试。"; length_scale = 1.0 } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:50100/api/v1/tts -Method Post -ContentType "application/json" -Body $body
```

The response contains `audio_url`, `audio_duration_ms`, `inference_ms`,
`cpu_time_ms`, `cpu_utilization_percent`, `real_time_factor`, and
`characters_per_second`. `real_time_factor` below `1` means the generated
audio is longer than the wall-clock synthesis time.

`cpu_utilization_percent` is normalized across all logical processors, matching
the machine-wide percentage users expect from Task Manager. The separate
`cpu_core_equivalents` value shows how many logical CPU cores the Piper process
used on average during that synthesis.

For real streaming, send the same JSON to `/api/v1/tts/stream`. The endpoint
returns raw 16-bit little-endian, mono PCM (`audio/L16`) at 22050 Hz. The
headers `X-Audio-Sample-Rate` and `X-Audio-Channels` describe the stream.

```powershell
curl.exe -X POST http://127.0.0.1:50100/api/v1/tts/stream -H "Content-Type: application/json" -d "{\"text\":\"流式语音测试\"}" --output output.pcm
```

## Batch Reports

The browser page has an internal Chinese test set and accepts a JSON test set
with either a top-level array or `{ "cases": [...] }`:

```json
[
  { "id": "short", "text": "欢迎使用本地语音合成服务。" },
  { "id": "slow", "text": "这条用例以较慢语速合成。", "length_scale": 1.3 }
]
```

The browser runs cases serially, provides audio players and optional 1-5 human
quality scores, then exports JSON or CSV. TTS does not have a truthful
automatic “accuracy” value: text is its input rather than a prediction. The
report therefore records success, time, CPU, audio duration, throughput, and
real-time factor; `manual_quality` is kept separate for listening evaluation.

`SHA256SUMS.txt` verifies files after extraction. The adjacent `.zip.sha256`
verifies the downloaded archive.
