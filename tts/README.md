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
text-to-speech, streaming playback, and single-run resource metrics page.

At startup, the service loads the Piper model into one persistent worker and
runs a short warm-up synthesis. Startup therefore takes a little longer, but
later requests reuse the loaded model instead of loading it again. Keeping the
model resident also means that the service continues to occupy its normal
model memory while it is idle; use `Stop-PiperTtsLite.ps1` to release it.

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
`characters_per_second`. It also contains `peak_working_set_mb` and
`memory_utilization_percent` for the Piper process. `real_time_factor` below
`1` means the generated audio is longer than the wall-clock synthesis time.

`cpu_utilization_percent` is normalized across all logical processors, matching
the machine-wide percentage users expect from Task Manager. The separate
`cpu_core_equivalents` value shows how many logical CPU cores the Piper process
used on average during that synthesis.

Memory values come from the persistent Piper worker's refreshed Windows working
set, not only from the small .NET web process. `peak_working_set_mb` is the
highest sampled resident working set during the request, sampled about every
25 ms, and `memory_utilization_percent` divides that value by the computer's
total physical memory. Because the model stays loaded, this is a process-level
resident-memory peak rather than memory newly allocated by that request.

For real streaming, send the same JSON to `/api/v1/tts/stream`. The endpoint
returns raw 16-bit little-endian, mono PCM (`audio/L16`) at 22050 Hz. The
headers `X-Audio-Sample-Rate` and `X-Audio-Channels` describe the stream.
`X-Stream-Request-Id` identifies the completed resource metrics available from
`GET /api/v1/tts/stream/metrics/{requestId}`. The browser combines those server
metrics with client-side first-byte, first-playable, stutter, and streaming RTF
measurements.

```powershell
curl.exe -X POST http://127.0.0.1:50100/api/v1/tts/stream -H "Content-Type: application/json" -d "{\"text\":\"流式语音测试\"}" --output output.pcm
```

`SHA256SUMS.txt` verifies files after extraction. The adjacent `.zip.sha256`
verifies the downloaded archive.
