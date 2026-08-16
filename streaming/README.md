# Sherpa Streaming ASR for Windows CPU

This is a portable, offline Chinese streaming ASR service. It contains a
self-contained Windows server, the `sherpa-onnx` runtime, and the int8
Zipformer 14M streaming model. It does not need Docker, WSL, Python, a GPU, or
an Internet connection after the ZIP has been extracted.

The bundled model is Chinese-only. It uses streaming transducer decoding and
supports per-session hotword/context biasing with `modified_beam_search`.

## Start

Extract `SherpaStreamingAsr-windows-x64.zip`, open the extracted directory, and
double-click `Start-SherpaStreamingAsr.cmd`. The local service starts at
`http://127.0.0.1:50200`.

Open that address in Edge or Chrome. Enter one hotword or phrase per line,
then select the microphone button. The page continuously sends 16 kHz PCM audio
over WebSocket and displays interim text and endpoint-finalized text. Browser
microphone permission works on this local address.

To select another port or allow LAN file/API access, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-SherpaStreamingAsr.ps1 -Port 50200
```

Use `-BindAddress 0.0.0.0` only when another computer on the LAN must access
the service. Browser microphone access from a non-local plain HTTP address is
usually blocked by browser security policy.

Use `Stop-SherpaStreamingAsr.ps1` to stop the background service.

## API

Health and capability information:

```powershell
Invoke-RestMethod http://127.0.0.1:50200/health
```

The streaming endpoint is a WebSocket at
`ws://127.0.0.1:50200/api/v1/asr/stream`. Send a JSON start message first,
followed by 16 kHz mono signed-16-bit little-endian PCM binary messages, and
finish with `{ "type": "stop" }`.

```json
{
  "type": "start",
  "hotwords": ["深度学习", "Sherpa ONNX"]
}
```

The server emits `ready`, `partial`, `final`, and `complete` JSON messages. A
`final` message is emitted when endpoint detection sees a pause; `complete`
contains session CPU and memory metrics.

For a non-streaming local call, post a 16 kHz mono PCM WAV file:

```powershell
curl.exe -X POST http://127.0.0.1:50200/api/v1/asr `
  -F "audio=@C:\audio\sample.wav" `
  -F "hotwords=深度学习`n语音识别"
```

## Metrics

`metrics.service_metrics.service_working_set_mb` and
`service_private_memory_mb` describe this deployment service process. They do
not represent every process on the computer. `machine_memory_used_mb` and
`machine_memory_utilization_percent` are a one-time whole-machine snapshot at
the end of the recognition session. This makes the difference explicit in the
web page as well.

`cpu_utilization_percent` is the process CPU time accumulated during one
session, normalized across logical CPUs. It is most meaningful when one user is
using the service at a time.

`SHA256SUMS.txt` verifies extracted package files. The adjacent `.zip.sha256`
file verifies the downloaded archive.

## Limits

- The default model is optimized for lightweight Chinese streaming ASR. It is
  not an English or mixed Chinese-English model.
- Hotwords bias decoding; they do not force text that is absent from the audio.
- The service serializes decoder work to keep CPU and memory use predictable on
  a 16 GB CPU-only deployment computer.
