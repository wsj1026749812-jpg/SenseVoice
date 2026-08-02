# SenseVoice Windows 离线容器包

这是基于官方 `iic/SenseVoiceSmall` 的 HTTP 服务封装。它构建出一个 **包含模型权重与 FSMN-VAD** 的 Linux Docker 镜像；将导出的 `.tar` 带到另一台 Windows 电脑并导入后，首次启动无需下载 Python 依赖或模型。CPU 和 NVIDIA GPU 使用同一镜像。

> Docker Desktop 必须运行在 **Linux containers** 模式。GPU 模式还需要 WSL 2 后端、兼容的 NVIDIA Windows 驱动，以及 Docker Desktop 的 GPU 支持。没有可用 NVIDIA GPU 时请选择 CPU 模式。

## 目录

- `Dockerfile`：构建含 CUDA PyTorch、服务和 SenseVoiceSmall 权重的单镜像
- `compose.cpu.yaml`：CPU 启动定义
- `compose.gpu.yaml`：NVIDIA GPU 启动定义
- `scripts/Build-Image.ps1`：构建并导出离线镜像
- `scripts/Import-And-Run.ps1`：兼容入口，调用新的部署脚本
- `scripts/Split-ArchiveForRelease.ps1`：将离线镜像切成可上传 GitHub Release 的分卷
- `deployment/Deploy-SenseVoice.ps1`：随镜像交付的一键部署启动脚本
- `scripts/Smoke-Test.ps1`：检查健康状态或提交一个音频文件
- `THIRD_PARTY_NOTICES.md`：模型与上游组件的归属说明

## 1. 在可联网的构建机制作离线镜像

安装并启动 Docker Desktop 后，在此目录打开 PowerShell：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Build-Image.ps1
```

脚本会构建 `sensevoice:offline`，下载 `iic/SenseVoiceSmall` 和 FSMN-VAD 到镜像层，并生成 `dist\sensevoice-offline-rootfs.tar`。模型下载会是构建中最耗时的一步，镜像和 tar 文件也会比较大；这是让目标机器可离线启动的必要代价。

也可不导出，直接以 Compose 运行：

```powershell
docker build --tag sensevoice:offline .
docker compose -f compose.cpu.yaml up -d
```

## 从 GitHub Release 安装

Release 中的镜像会拆分为多个 `sensevoice-offline-rootfs.tar.partNNN` 文件，以满足 GitHub 单个附件的大小限制。下载 **全部** 分卷、`Deploy-SenseVoice.ps1` 和 `SHA256SUMS.txt` 到同一目录，然后执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-SenseVoice.ps1 -Mode cpu
```

脚本会自动合并分卷、导入 Docker 镜像并等待健康检查通过。GPU 模式只需把参数改为 `-Mode gpu`。合并时需要额外约一个镜像归档大小的临时磁盘空间；合并完成后可保留或手动删除分卷。

## 2. 在目标 Windows 电脑启动

目标机仅需 Docker Desktop，不需要 Python、CUDA Toolkit 或 Git。构建脚本会在 `dist` 中生成两个需要交付的文件：`sensevoice-offline-rootfs.tar` 与 `Deploy-SenseVoice.ps1`。把它们复制到同一目录，在该目录打开 PowerShell 后执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-SenseVoice.ps1 -Mode cpu
```

若该电脑安装了 NVIDIA GPU，且 Docker Desktop 已经能使用 GPU：

```powershell
powershell -ExecutionPolicy Bypass -File .\Deploy-SenseVoice.ps1 -Mode gpu
```

服务地址为 `http://localhost:50000`；交互式接口为 `http://localhost:50000/docs`。首次启动时会把镜像中封装的模型复制到 Docker 卷，随后启动较快。模型卷会跨容器重建保留；要完全清理它，请明确执行 `docker volume rm sensevoice-models`。

默认只允许本机访问（绑定 `127.0.0.1`）。确实需要让局域网设备访问时，在脚本中加上 `-BindAddress 0.0.0.0`；Compose 则设置 `SENSEVOICE_BIND=0.0.0.0`。

默认 GPU 模式使用 `cuda:0`，默认最多同时处理 1 个请求、每批 60 秒音频。这一配置为显存较小的显卡留出了余地；16GB 显存通常不需要额外调小。CPU 模式可用，但处理速度取决于 CPU 性能和音频时长。

## 3. 验证与调用

等待健康检查变为 `ok`：

```powershell
.\scripts\Smoke-Test.ps1
```

提交音频（支持 WAV、MP3、M4A、FLAC、OGG、OPUS 和 AAC）：

```powershell
.\scripts\Smoke-Test.ps1 -AudioPath C:\audio\sample.wav
```

也可以直接使用 curl：

```powershell
curl.exe -X POST http://localhost:50000/api/v1/asr `
  -F "files=@C:\audio\sample.wav" `
  -F "lang=auto" `
  -F "use_itn=true"
```

`lang` 可用值：`auto`、`zh`、`en`、`yue`、`ja`、`ko`、`nospeech`。返回的 `text` 是可读转写，`raw_text` 保留 SenseVoice 的语言、情感和事件标记，`clean_text` 则只移除这些标记。

## Compose 启动

已导入或已构建 `sensevoice:offline` 后，也可选择：

```powershell
docker compose -f compose.cpu.yaml up -d
docker compose -f compose.gpu.yaml up -d
```

两者只能选择一个运行。GPU Compose 启动失败通常表示 Docker Desktop 尚未获得 GPU：先在 PowerShell 运行 `docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi` 验证环境，再启动服务。

## 运维参数

可通过环境变量调整：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `SENSEVOICE_DEVICE` | `auto` | `cpu`、`cuda:0` 或 `auto`。显式要求 CUDA 却不存在时服务会拒绝启动。 |
| `MAX_CONCURRENT_REQUESTS` | `1` | 同时推理数。显存紧张时保持 1。 |
| `BATCH_SIZE_S` | `60` | 单批音频秒数。长音频显存不足时调低，例如 `30`。 |
| `SENSEVOICE_ALLOW_CPU_FALLBACK` | `false` | 显式 CUDA 不可用时是否降级到 CPU。 |

## 参考

- [FunAudioLLM/SenseVoice 官方仓库](https://github.com/FunAudioLLM/SenseVoice)
- [官方 FastAPI/Docker 实现](https://github.com/FunAudioLLM/SenseVoice/blob/main/api.py)
- [Docker Desktop GPU 支持文档](https://docs.docker.com/desktop/features/gpu/)
