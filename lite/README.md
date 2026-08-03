# SenseVoice Lite for Windows CPU

This package runs the official `llama-funasr-sensevoice` GGUF runtime without
Docker, WSL, Python, PyTorch, or a GPU. It uses the official Q8 SenseVoiceSmall
model and the built-in native FSMN-VAD path.

## Start

Extract the ZIP, open the extracted `SenseVoiceLite-windows-x64` directory,
then double-click `Start-SenseVoiceLite.cmd`. It starts a local-only service at
`http://127.0.0.1:50000`.

To choose a port or bind to the LAN, run the deployment script from the
extracted package directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-SenseVoiceLite.ps1
```

For example, use `-Port 50100` to choose another local port. Use
`-BindAddress 0.0.0.0` only when other machines on the LAN need access.

```powershell
Invoke-RestMethod http://127.0.0.1:50000/health
curl.exe -X POST http://127.0.0.1:50000/api/v1/asr -F "files=@C:\audio\sample.wav" -F "lang=auto"
```

Use `Stop-SenseVoiceLite.ps1` to stop the background service.

`SHA256SUMS.txt` verifies the extracted package files. The adjacent
`SenseVoiceLite-windows-x64.zip.sha256` verifies the downloaded archive.

## Limits

- Audio must be a 16 kHz, mono, PCM WAV file. This is the input format accepted
  by the official native runtime.
- Requests are handled one at a time to keep memory use predictable.
- The native runtime uses automatic language detection. Send `lang=auto`.
- `raw_text` retains SenseVoice tags. `clean_text` and `text` remove those tags;
  `language`, `emotion`, `event`, and `itn` expose their values separately.
