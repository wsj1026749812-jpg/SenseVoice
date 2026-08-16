const elements = {
  health: document.querySelector("#healthStatus"),
  source: document.querySelector("#sourceStatus"),
  hotwords: document.querySelector("#hotwords"),
  startMic: document.querySelector("#startMic"),
  stopMic: document.querySelector("#stopMic"),
  clear: document.querySelector("#clearText"),
  file: document.querySelector("#audioFile"),
  startFile: document.querySelector("#startFile"),
  final: document.querySelector("#finalText"),
  partial: document.querySelector("#partialText"),
  elapsed: document.querySelector("#elapsedMetric"),
  cpu: document.querySelector("#cpuMetric"),
  serviceMemory: document.querySelector("#serviceMemoryMetric"),
  machineMemory: document.querySelector("#machineMemoryMetric"),
};

let recorder = null;
let activeSocket = null;
let activeSource = null;

function wsUrl() {
  const protocol = location.protocol === "https:" ? "wss:" : "ws:";
  return `${protocol}//${location.host}/api/v1/asr/stream`;
}

function hotwords() {
  return elements.hotwords.value.split(/\r?\n|,|，/).map(value => value.trim()).filter(Boolean).slice(0, 50);
}

function setState(value) {
  elements.source.textContent = value;
  const active = value === "正在听写" || value === "正在识别文件" || value === "正在连接";
  elements.startMic.disabled = active;
  elements.stopMic.disabled = !active;
  elements.startFile.disabled = active || !elements.file.files.length;
}

function appendFinal(text) {
  const value = (text || "").trim();
  if (!value) return;
  const existing = elements.final.textContent.trimEnd();
  if (existing.endsWith(value)) return;
  elements.final.textContent = existing ? `${existing}\n${value}` : value;
}

function resetMetrics() {
  elements.elapsed.textContent = "-";
  elements.cpu.textContent = "-";
  elements.serviceMemory.textContent = "-";
  elements.machineMemory.textContent = "-";
}

function showMetrics(metrics) {
  if (!metrics) return;
  const memory = metrics.serviceMetrics || metrics.service_metrics;
  elements.elapsed.textContent = `${format(metrics.elapsedMs ?? metrics.elapsed_ms)} ms`;
  elements.cpu.textContent = `${format(metrics.cpuUtilizationPercent ?? metrics.cpu_utilization_percent)}%`;
  elements.serviceMemory.textContent = memory ? `${format(memory.serviceWorkingSetMb ?? memory.service_working_set_mb)} MB` : "-";
  elements.machineMemory.textContent = memory ? `${format(memory.machineMemoryUtilizationPercent ?? memory.machine_memory_utilization_percent)}%` : "-";
}

function format(value) {
  return Number.isFinite(Number(value)) ? Number(value).toLocaleString("zh-CN", { maximumFractionDigits: 1 }) : "-";
}

function connect(source) {
  resetMetrics();
  elements.partial.textContent = "";
  activeSource = source;
  setState("正在连接");
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(wsUrl());
    socket.binaryType = "arraybuffer";
    activeSocket = socket;

    socket.addEventListener("open", () => {
      socket.send(JSON.stringify({ type: "start", hotwords: hotwords() }));
    });
    socket.addEventListener("message", event => {
      const message = JSON.parse(event.data);
      if (message.type === "ready") {
        setState(source === "mic" ? "正在听写" : "正在识别文件");
        resolve(socket);
      } else if (message.type === "partial") {
        elements.partial.textContent = message.text || "";
      } else if (message.type === "final") {
        appendFinal(message.text);
        elements.partial.textContent = "";
      } else if (message.type === "complete") {
        appendFinal(message.text);
        elements.partial.textContent = "";
        showMetrics(message.metrics);
      } else if (message.type === "error") {
        reject(new Error(message.detail || "服务返回错误"));
      }
    });
    socket.addEventListener("error", () => reject(new Error("无法连接流式识别服务。")), { once: true });
    socket.addEventListener("close", () => {
      if (activeSocket === socket) {
        activeSocket = null;
        activeSource = null;
        if (recorder) stopRecorder();
        setState("空闲");
      }
    });
  });
}

async function startMicrophone() {
  try {
    const socket = await connect("mic");
    const media = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
    const context = new AudioContext();
    const input = context.createMediaStreamSource(media);
    const processor = context.createScriptProcessor(4096, 1, 1);
    processor.onaudioprocess = event => {
      if (socket.readyState !== WebSocket.OPEN) return;
      const source = event.inputBuffer.getChannelData(0);
      socket.send(toPcm16(resample(source, context.sampleRate, 16000)));
    };
    input.connect(processor);
    processor.connect(context.destination);
    recorder = { context, input, processor, media };
  } catch (error) {
    closeActiveSocket();
    setState("连接失败");
    elements.partial.textContent = error.message;
  }
}

function stopRecorder() {
  if (!recorder) return;
  recorder.processor.disconnect();
  recorder.input.disconnect();
  recorder.media.getTracks().forEach(track => track.stop());
  recorder.context.close();
  recorder = null;
}

function closeActiveSocket() {
  stopRecorder();
  if (activeSocket && activeSocket.readyState === WebSocket.OPEN) {
    activeSocket.send(JSON.stringify({ type: "stop" }));
  }
}

async function startFile() {
  const file = elements.file.files[0];
  if (!file) return;
  try {
    const socket = await connect("file");
    const context = new AudioContext();
    const decoded = await context.decodeAudioData(await file.arrayBuffer());
    const mono = mixToMono(decoded);
    const pcm = toPcm16(resample(mono, decoded.sampleRate, 16000));
    const chunkBytes = 3200;
    for (let offset = 0; offset < pcm.byteLength; offset += chunkBytes) {
      if (socket.readyState !== WebSocket.OPEN) break;
      socket.send(pcm.slice(offset, Math.min(offset + chunkBytes, pcm.byteLength)));
    }
    await context.close();
    if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: "stop" }));
  } catch (error) {
    closeActiveSocket();
    setState("识别失败");
    elements.partial.textContent = error.message;
  }
}

function mixToMono(buffer) {
  const output = new Float32Array(buffer.length);
  for (let channel = 0; channel < buffer.numberOfChannels; channel += 1) {
    const input = buffer.getChannelData(channel);
    for (let index = 0; index < input.length; index += 1) output[index] += input[index] / buffer.numberOfChannels;
  }
  return output;
}

function resample(input, sourceRate, targetRate) {
  if (sourceRate === targetRate) return input;
  const length = Math.round(input.length * targetRate / sourceRate);
  const output = new Float32Array(length);
  const ratio = sourceRate / targetRate;
  for (let index = 0; index < length; index += 1) {
    const position = index * ratio;
    const left = Math.floor(position);
    const right = Math.min(left + 1, input.length - 1);
    output[index] = input[left] + (input[right] - input[left]) * (position - left);
  }
  return output;
}

function toPcm16(samples) {
  const output = new ArrayBuffer(samples.length * 2);
  const view = new DataView(output);
  for (let index = 0; index < samples.length; index += 1) {
    const value = Math.max(-1, Math.min(1, samples[index]));
    view.setInt16(index * 2, value < 0 ? value * 32768 : value * 32767, true);
  }
  return output;
}

elements.startMic.addEventListener("click", startMicrophone);
elements.stopMic.addEventListener("click", closeActiveSocket);
elements.clear.addEventListener("click", () => {
  elements.final.textContent = "";
  elements.partial.textContent = "";
  resetMetrics();
});
elements.file.addEventListener("change", () => setState("空闲"));
elements.startFile.addEventListener("click", startFile);

fetch("/health")
  .then(response => response.ok ? response.json() : Promise.reject(new Error("服务不可用")))
  .then(() => {
    elements.health.textContent = "服务就绪 · CPU";
    elements.health.className = "health ok";
  })
  .catch(() => {
    elements.health.textContent = "服务不可用";
    elements.health.className = "health error";
  });
setState("空闲");
