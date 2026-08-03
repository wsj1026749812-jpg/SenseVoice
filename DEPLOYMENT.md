# SenseVoice Lite Windows 部署说明

本文说明如何在原生 Windows 电脑上部署 `v1.1.1-lite`。该版本使用官方
SenseVoice llama.cpp/GGUF 原生运行时、Q8 模型和 FSMN-VAD 模型。

模型 API 服务和网页测试前端由同一个自包含进程提供，**不需要单独部署或启动
前端服务**。

## 1. 部署要求

部署电脑需要满足：

- 64 位 Windows 10 或 Windows 11
- Intel 或 AMD x64 CPU
- 建议内存 16GB
- 至少 500MB 空闲磁盘空间
- 用于测试网页的 Microsoft Edge 或 Google Chrome
- 原始 API 音频输入为 16kHz、单声道、PCM WAV

不需要安装 Docker、WSL、Python、PyTorch、CUDA、Node.js、npm、Git 或 .NET
Runtime。ZIP 已包含模型权重、原生推理程序、HTTP 服务和网页前端文件。

## 2. 下载与校验

从 Release 页面下载以下两个文件：

- `SenseVoiceLite-windows-x64.zip`
- `SenseVoiceLite-windows-x64.zip.sha256`

Release 地址：
https://github.com/wsj1026749812-jpg/SenseVoice/releases/tag/v1.1.1-lite

在 PowerShell 中校验 ZIP 文件：

```powershell
Get-FileHash .\SenseVoiceLite-windows-x64.zip -Algorithm SHA256
```

`v1.1.1-lite` 的正确 SHA-256 为：

```text
ba4e8ef1916a9b0b11e1d3e6745b484a3dbe057f82323a9682251472c8dce7dd
```

校验一致后，将 ZIP 解压到可写入的本地目录，例如 `D:\SenseVoiceLite`。不要
解压到受系统保护的目录。

## 3. 启动模型与前端服务

打开解压后的 `SenseVoiceLite-windows-x64` 目录，双击：

```text
Start-SenseVoiceLite.cmd
```

这会启动一个本地进程，同时提供模型 API 和网页前端：

| 服务 | 地址 |
| --- | --- |
| 网页测试前端 | `http://127.0.0.1:50000` |
| 健康检查 | `http://127.0.0.1:50000/health` |
| ASR API | `http://127.0.0.1:50000/api/v1/asr` |

不需要管理员权限。默认仅允许本机访问，局域网内其他电脑无法连接。

停止服务：

```powershell
powershell -ExecutionPolicy Bypass -File .\Stop-SenseVoiceLite.ps1
```

## 4. 网页前端测试

在部署电脑的 Edge 或 Chrome 中打开：

```text
http://127.0.0.1:50000
```

网页可使用麦克风录音或选择本地音频文件。提交到模型前，浏览器会自动将音频
转换为 16kHz、单声道、PCM WAV。

1. 点击 `Record`，在浏览器弹窗中允许麦克风权限。
2. 说一句话后点击 `Stop`。
3. 点击 `Run transcription`。
4. 可在 `Reference transcript` 中粘贴人工正确文本。

页面会显示：

- `Service time`：该次请求的端到端 API 耗时。
- `Realtime speed`：音频时长除以服务耗时。`1x` 表示实时，数值大于 `1x`
  表示处理速度快于实时。
- `Character accuracy`：与参考文本的字符级对比。会忽略空白字符；应保持标点
  一致，结果才有可比性。
- `Language`、`Emotion`、`Event`、`ITN`：官方原生运行时返回的 SenseVoice
  标签。

麦克风测试必须在部署机本地通过 `http://127.0.0.1:50000` 打开。浏览器通常会
阻止来自其他电脑的普通 HTTP 页面调用麦克风。

## 5. 模型 API 测试

先检查服务是否就绪：

```powershell
Invoke-RestMethod http://127.0.0.1:50000/health
```

正常响应包含 `status: ok`、`device: cpu` 和 `runtime: llama.cpp/GGUF`。

提交一个 WAV 文件：

```powershell
curl.exe -X POST http://127.0.0.1:50000/api/v1/asr `
  -F "files=@C:\audio\sample.wav" `
  -F "lang=auto"
```

API 可接收一个或多个名称为 `files` 的 multipart 文件字段。当前原生运行时仅
使用自动语言识别，因此请传入 `lang=auto`。服务按单请求顺序推理，以使 CPU
和内存占用保持可预测。

响应示例：

```json
{
  "result": [
    {
      "filename": "sample.wav",
      "text": "甚至出现交易几乎停滞的情况。",
      "language": "zh",
      "emotion": "NEUTRAL",
      "event": "Speech",
      "itn": "withitn"
    }
  ]
}
```

`raw_text` 保留原始 SenseVoice 标签；`clean_text` 和 `text` 会移除标签。

## 6. 可选的局域网访问

仅在可信局域网中使用。先停止当前服务，再执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-SenseVoiceLite.ps1 `
  -BindAddress 0.0.0.0 -Port 50000
```

仅为需要的专用网络放行 Windows 防火墙端口。局域网电脑可以向 API 上传兼容的
WAV 文件。若要在另一台电脑的浏览器中使用麦克风，需要将服务置于 HTTPS 之后；
普通 HTTP 不满足浏览器的麦克风安全要求。

## 7. 常见问题

### 端口已被占用

换一个端口启动：

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-SenseVoiceLite.ps1 -Port 50100
```

随后访问 `http://127.0.0.1:50100`。

### 服务没有就绪

检查解压目录下的日志：

```text
logs\server.stdout.log
logs\server.stderr.log
```

### API 拒绝音频

原始 API 仅接收 16kHz、单声道、PCM WAV。可使用网页测试前端处理浏览器可解码
的音频格式，或先在调用 API 前完成转换。

### Windows SmartScreen 提示

包内包含本地构建的可执行文件。先核对上述 SHA-256；只有校验值与 Release
一致时，才在 SmartScreen 中选择“更多信息”与“仍要运行”。

## 8. 交付包内容

```text
SenseVoiceLite.Server.exe       自包含 API 与静态前端服务
runtime\                         官方 llama-funasr-sensevoice 可执行文件
models\                          Q8 SenseVoiceSmall 与 FSMN-VAD GGUF 模型
wwwroot\                         麦克风、速度、准确率测试前端页面
Start-SenseVoiceLite.cmd        双击启动脚本
Deploy-SenseVoiceLite.ps1       支持自定义地址和端口的启动脚本
Stop-SenseVoiceLite.ps1         停止脚本
SHA256SUMS.txt                  解压后各文件的校验清单
```

## 源码与归属

- 部署项目：https://github.com/wsj1026749812-jpg/SenseVoice
- 官方 SenseVoice 运行时：https://github.com/QwenAudio/SenseVoice
- Q8 模型：https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF
- VAD 模型：https://huggingface.co/FunAudioLLM/fsmn-vad-GGUF
