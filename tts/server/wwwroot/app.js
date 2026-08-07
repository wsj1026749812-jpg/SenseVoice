const state = {
  audioContext: null,
  streamNextTime: 0,
};

const $ = (selector) => document.querySelector(selector);
const textInput = $("#text");
const status = $("#single-status");

function decimal(value, digits = 1) {
  return Number(value).toFixed(digits);
}

function formatMs(value) {
  return `${decimal(value)} ms`;
}

function requestPayload() {
  return {
    text: textInput.value.trim(),
    length_scale: Number($("#length-scale").value),
    noise_scale: Number($("#noise-scale").value),
    noise_w_scale: 0.8,
  };
}

function setStatus(message, isError = false) {
  status.textContent = message;
  status.classList.toggle("error", isError);
}

function setBusy(busy, message = "") {
  $("#synthesize").disabled = busy;
  $("#stream").disabled = busy;
  if (message) setStatus(message);
}

function updateCounter() {
  $(".counter").textContent = `${textInput.value.length} / 2000`;
}

function updateSliders() {
  $("#length-scale-value").textContent = `${decimal($("#length-scale").value)}x`;
  $("#noise-scale-value").textContent = decimal($("#noise-scale").value, 2);
}

async function api(url, body) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    const detail = await response.json().catch(() => ({}));
    throw new Error(detail.detail || `请求失败 (${response.status})`);
  }
  return response;
}

function showMetrics(result) {
  const metrics = [
    ["音频时长", `${decimal(result.audio_duration_ms / 1000, 2)} s`],
    ["合成耗时", formatMs(result.inference_ms)],
    ["CPU 时间", formatMs(result.cpu_time_ms)],
    ["CPU 占用", `${decimal(result.cpu_utilization_percent)}%`],
    ["逻辑核心占用", `${decimal(result.cpu_core_equivalents, 2)} / ${result.processor_count} 核`],
    ["峰值内存", `${decimal(result.peak_working_set_mb)} MB`],
    ["内存占用率", `${decimal(result.memory_utilization_percent, 2)}%`],
    ["实时系数", decimal(result.real_time_factor, 3)],
    ["字符吞吐", `${decimal(result.characters_per_second)} 字/秒`],
    ["采样率", `${result.sample_rate} Hz`],
  ];
  $("#metrics").classList.remove("empty");
  $("#metrics").innerHTML = metrics.map(([label, value]) => `<div class="metric"><span>${label}</span><strong>${value}</strong></div>`).join("");
}

async function synthesize() {
  const payload = requestPayload();
  if (!payload.text) {
    setStatus("请输入需要合成的文本。", true);
    return;
  }
  setBusy(true, "正在生成 WAV...");
  try {
    const result = await (await api("/api/v1/tts", payload)).json();
    const audio = $("#audio");
    audio.src = result.audio_url;
    audio.classList.remove("is-hidden");
    const download = $("#download");
    download.href = result.download_url;
    download.download = result.filename;
    download.classList.remove("is-hidden");
    showMetrics(result);
    setStatus(`已生成 ${result.filename}`);
  } catch (error) {
    setStatus(error.message, true);
  } finally {
    setBusy(false);
  }
}

function decodePcm(buffer, sampleRate) {
  const input = new Int16Array(buffer);
  const audioBuffer = state.audioContext.createBuffer(1, input.length, sampleRate);
  const channel = audioBuffer.getChannelData(0);
  for (let index = 0; index < input.length; index += 1) channel[index] = input[index] / 32768;
  return audioBuffer;
}

function queuePcm(buffer, sampleRate) {
  if (!buffer.byteLength) return { duration: 0, startAt: state.audioContext.currentTime };
  const audioBuffer = decodePcm(buffer, sampleRate);
  const source = state.audioContext.createBufferSource();
  source.buffer = audioBuffer;
  source.connect(state.audioContext.destination);
  const startAt = Math.max(state.audioContext.currentTime + 0.04, state.streamNextTime);
  source.start(startAt);
  state.streamNextTime = startAt + audioBuffer.duration;
  return { duration: audioBuffer.duration, startAt };
}

async function waitForStreamMetrics(requestId) {
  if (!requestId) return null;
  for (let attempt = 0; attempt < 30; attempt += 1) {
    const response = await fetch(`/api/v1/tts/stream/metrics/${encodeURIComponent(requestId)}`, { cache: "no-store" });
    const payload = await response.json().catch(() => ({}));
    if (response.ok && payload.status === "complete") return payload.metrics;
    if (response.status !== 202) throw new Error(payload.detail || `无法读取流式指标 (${response.status})`);
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error("流式资源指标尚未就绪。");
}

async function streamAudio() {
  const payload = requestPayload();
  if (!payload.text) {
    setStatus("请输入需要合成的文本。", true);
    return;
  }
  setBusy(true, "正在建立流式播放...");
  const start = performance.now();
  try {
    const response = await api("/api/v1/tts/stream", payload);
    const sampleRate = Number(response.headers.get("X-Audio-Sample-Rate") || 22050);
    const requestId = response.headers.get("X-Stream-Request-Id");
    if (!response.body) throw new Error("浏览器不支持流式响应。");
    state.audioContext ||= new AudioContext();
    await state.audioContext.resume();
    state.streamNextTime = state.audioContext.currentTime;
    const reader = response.body.getReader();
    let remainder = new Uint8Array(0);
    let audioSeconds = 0;
    let firstByteMs = null;
    let firstPlayableMs = null;
    let queuedChunks = 0;
    let stutterCount = 0;
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      if (firstByteMs === null && value.byteLength) firstByteMs = performance.now() - start;
      const combined = new Uint8Array(remainder.length + value.length);
      combined.set(remainder);
      combined.set(value, remainder.length);
      const usableLength = combined.length - (combined.length % 2);
      if (usableLength) {
        if (queuedChunks && state.audioContext.currentTime > state.streamNextTime + 0.02) stutterCount += 1;
        const queued = queuePcm(combined.slice(0, usableLength).buffer, sampleRate);
        audioSeconds += queued.duration;
        queuedChunks += 1;
        if (firstPlayableMs === null) {
          firstPlayableMs = performance.now() - start + Math.max(0, queued.startAt - state.audioContext.currentTime) * 1000;
        }
      }
      remainder = combined.slice(usableLength);
    }
    const elapsed = performance.now() - start;
    const serverMetrics = await waitForStreamMetrics(requestId);
    const streamElapsedMs = serverMetrics?.elapsedMs || elapsed;
    const streamRtf = streamElapsedMs / Math.max(audioSeconds * 1000, 1);
    $("#metrics").classList.remove("empty");
    $("#metrics").innerHTML = [
      ["首包延迟", firstByteMs === null ? "--" : formatMs(firstByteMs)],
      ["首次可播放延迟", firstPlayableMs === null ? "--" : formatMs(firstPlayableMs)],
      ["播放卡顿", stutterCount ? `是（${stutterCount} 次）` : "否"],
      ["流式 RTF", decimal(streamRtf, 3)],
      ["CPU 占用", serverMetrics ? `${decimal(serverMetrics.cpuUtilizationPercent)}%` : "--"],
      ["峰值内存", serverMetrics ? `${decimal(serverMetrics.peakWorkingSetMb)} MB` : "--"],
      ["内存占用率", serverMetrics ? `${decimal(serverMetrics.memoryUtilizationPercent, 2)}%` : "--"],
      ["流式音频", `${decimal(audioSeconds, 2)} s`],
    ].map(([label, value]) => `<div class="metric"><span>${label}</span><strong>${value}</strong></div>`).join("");
    setStatus("流式合成完成，音频正在播放或已排队。");
  } catch (error) {
    setStatus(error.message, true);
  } finally {
    setBusy(false);
  }
}

async function checkHealth() {
  try {
    const health = await (await fetch("/health", { cache: "no-store" })).json();
    $("#health").textContent = `CPU 就绪 | ${health.voice.id}`;
  } catch {
    $("#health").textContent = "服务不可用";
    $("#health").classList.add("error");
  }
}

textInput.addEventListener("input", updateCounter);
$("#length-scale").addEventListener("input", updateSliders);
$("#noise-scale").addEventListener("input", updateSliders);
$("#synthesize").addEventListener("click", synthesize);
$("#stream").addEventListener("click", streamAudio);
$("#clear").addEventListener("click", () => { textInput.value = ""; updateCounter(); textInput.focus(); });
updateCounter();
updateSliders();
checkHealth();
