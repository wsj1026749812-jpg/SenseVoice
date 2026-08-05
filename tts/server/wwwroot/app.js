const state = {
  batchCases: [],
  batchResults: [],
  audioContext: null,
  streamNextTime: 0,
};

const builtInCases = [
  { id: "short-zh", text: "您好，欢迎使用本地中文语音合成服务。" },
  { id: "number-mix", text: "今天是二零二六年八月六日，订单编号为 SV-1024，金额一百二十八点五元。" },
  { id: "paragraph-zh", text: "本测试在不使用 GPU、Docker 或外部网络服务的条件下运行。它记录端到端合成耗时、进程 CPU 时间、生成音频时长和实时系数，方便比较不同电脑的本地推理性能。" },
];

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
  $("#run-batch").disabled = busy || !state.batchCases.length;
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
    ["端到端耗时", formatMs(result.inference_ms)],
    ["CPU 占用", `${decimal(result.cpu_utilization_percent)}%`],
    ["逻辑核心占用", `${decimal(result.cpu_core_equivalents, 2)} 核`],
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
  if (!buffer.byteLength) return 0;
  const audioBuffer = decodePcm(buffer, sampleRate);
  const source = state.audioContext.createBufferSource();
  source.buffer = audioBuffer;
  source.connect(state.audioContext.destination);
  const startAt = Math.max(state.audioContext.currentTime + 0.04, state.streamNextTime);
  source.start(startAt);
  state.streamNextTime = startAt + audioBuffer.duration;
  return audioBuffer.duration;
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
    if (!response.body) throw new Error("浏览器不支持流式响应。");
    state.audioContext ||= new AudioContext();
    await state.audioContext.resume();
    state.streamNextTime = state.audioContext.currentTime;
    const reader = response.body.getReader();
    let remainder = new Uint8Array(0);
    let audioSeconds = 0;
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      const combined = new Uint8Array(remainder.length + value.length);
      combined.set(remainder);
      combined.set(value, remainder.length);
      const usableLength = combined.length - (combined.length % 2);
      if (usableLength) audioSeconds += queuePcm(combined.slice(0, usableLength).buffer, sampleRate);
      remainder = combined.slice(usableLength);
    }
    const elapsed = performance.now() - start;
    $("#metrics").classList.remove("empty");
    $("#metrics").innerHTML = [
      ["流式音频", `${decimal(audioSeconds, 2)} s`],
      ["首尾请求耗时", formatMs(elapsed)],
      ["数据格式", `${sampleRate} Hz / PCM`],
      ["播放状态", "已排队"],
    ].map(([label, value]) => `<div class="metric"><span>${label}</span><strong>${value}</strong></div>`).join("");
    setStatus("流式音频已开始播放。");
  } catch (error) {
    setStatus(error.message, true);
  } finally {
    setBusy(false);
  }
}

function renderBatch() {
  const body = $("#batch-body");
  if (!state.batchResults.length) {
    body.innerHTML = '<tr><td colspan="7" class="placeholder">暂无测试结果</td></tr>';
    return;
  }
  body.innerHTML = state.batchResults.map((result, index) => {
    if (result.error) {
      return `<tr><td>${escapeHtml(result.id)}</td><td class="row-text">${escapeHtml(result.text)}</td><td colspan="5" class="status error">${escapeHtml(result.error)}</td></tr>`;
    }
    return `<tr>
      <td>${escapeHtml(result.id)}</td>
      <td class="row-text">${escapeHtml(result.text)}</td>
      <td><audio class="row-audio" controls src="${result.audio_url}"></audio></td>
      <td>${formatMs(result.inference_ms)}<br><small>${decimal(result.characters_per_second)} 字/秒</small></td>
      <td>${decimal(result.cpu_utilization_percent)}%</td>
      <td>${decimal(result.real_time_factor, 3)}</td>
      <td><select class="rating" data-index="${index}" aria-label="${escapeHtml(result.id)} 人工音质评分"><option value="">未评分</option><option value="1">1 / 5</option><option value="2">2 / 5</option><option value="3">3 / 5</option><option value="4">4 / 5</option><option value="5">5 / 5</option></select></td>
    </tr>`;
  }).join("");
  body.querySelectorAll(".rating").forEach((select) => {
    select.value = state.batchResults[Number(select.dataset.index)].manual_quality || "";
    select.addEventListener("change", () => {
      state.batchResults[Number(select.dataset.index)].manual_quality = select.value ? Number(select.value) : null;
      updateSummary();
    });
  });
}

function updateSummary() {
  const completed = state.batchResults.filter((item) => !item.error);
  const sum = (field) => completed.reduce((total, item) => total + Number(item[field] || 0), 0);
  const rated = completed.filter((item) => item.manual_quality);
  const totalElapsed = sum("inference_ms");
  const totalAudio = sum("audio_duration_ms");
  const processorCount = completed[0]?.processor_count || 1;
  const values = [
    ["完成", `${completed.length} / ${state.batchResults.length}`],
    ["合成总耗时", formatMs(totalElapsed)],
    ["音频总时长", `${decimal(totalAudio / 1000, 2)} s`],
    ["加权 CPU", completed.length ? `${decimal(sum("cpu_time_ms") / Math.max(totalElapsed * processorCount, 1) * 100)}%` : "-"],
    ["人工音质均分", rated.length ? `${decimal(rated.reduce((total, item) => total + item.manual_quality, 0) / rated.length, 2)} / 5` : "未评分"],
  ];
  const summary = $("#batch-summary");
  summary.classList.remove("empty");
  summary.innerHTML = values.map(([label, value]) => `<div class="summary-item"><span>${label}</span><strong>${value}</strong></div>`).join("");
}

async function runBatch() {
  if (!state.batchCases.length) return;
  state.batchResults = [];
  renderBatch();
  setBusy(true, "批量测试准备中...");
  for (let index = 0; index < state.batchCases.length; index += 1) {
    const testCase = state.batchCases[index];
    setStatus(`正在测试 ${index + 1} / ${state.batchCases.length}: ${testCase.id}`);
    try {
      const response = await api("/api/v1/tts", {
        text: testCase.text,
        length_scale: Number(testCase.length_scale || 1),
        noise_scale: Number(testCase.noise_scale || 0.667),
        noise_w_scale: Number(testCase.noise_w_scale || 0.8),
      });
      state.batchResults.push({ id: testCase.id, ...await response.json(), manual_quality: null });
    } catch (error) {
      state.batchResults.push({ id: testCase.id, text: testCase.text, error: error.message, manual_quality: null });
    }
    renderBatch();
    updateSummary();
  }
  $("#export-json").disabled = !state.batchResults.length;
  $("#export-csv").disabled = !state.batchResults.length;
  setBusy(false);
  setStatus(`批量测试完成：${state.batchResults.filter((item) => !item.error).length} 条成功。`);
}

function loadCases(cases, description) {
  const validated = cases.map((item, index) => ({
    id: String(item.id || `case-${index + 1}`),
    text: String(item.text || "").trim(),
    length_scale: item.length_scale,
    noise_scale: item.noise_scale,
    noise_w_scale: item.noise_w_scale,
  })).filter((item) => item.text);
  if (!validated.length) throw new Error("测试集至少需要一条含 text 的记录。");
  if (validated.some((item) => item.text.length > 2000)) throw new Error("每条测试文本必须少于 2000 个字符。");
  state.batchCases = validated;
  state.batchResults = [];
  renderBatch();
  $("#batch-description").textContent = description || `已载入 ${validated.length} 条测试用例。`;
  $("#batch-summary").className = "summary empty";
  $("#batch-summary").textContent = `已载入 ${validated.length} 条测试用例，点击“开始测试”。`;
  $("#run-batch").disabled = false;
  $("#export-json").disabled = true;
  $("#export-csv").disabled = true;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>'"]/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[character]));
}

function report() {
  const success = state.batchResults.filter((item) => !item.error);
  const totalInferenceMs = success.reduce((sum, item) => sum + item.inference_ms, 0);
  const processorCount = success[0]?.processor_count || null;
  return {
    report_type: "Piper TTS Lite CPU benchmark",
    generated_at: new Date().toISOString(),
    device: "cpu",
    runtime: "Piper/ONNX Runtime",
    streaming_tested: false,
    note: "TTS has no automatic text accuracy metric. Optional manual_quality is a listener score from 1 to 5.",
    summary: {
      total_cases: state.batchResults.length,
      successful_cases: success.length,
      total_inference_ms: totalInferenceMs,
      total_audio_duration_ms: success.reduce((sum, item) => sum + item.audio_duration_ms, 0),
      processor_count: processorCount,
      weighted_cpu_utilization_percent: success.length ? success.reduce((sum, item) => sum + item.cpu_time_ms, 0) / Math.max(totalInferenceMs * (processorCount || 1), 1) * 100 : null,
    },
    results: state.batchResults,
  };
}

function downloadBlob(content, name, type) {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = name;
  anchor.click();
  URL.revokeObjectURL(url);
}

function exportJson() {
  downloadBlob(JSON.stringify(report(), null, 2), `piper-tts-benchmark-${Date.now()}.json`, "application/json");
}

function exportCsv() {
  const headers = ["id", "text", "filename", "audio_duration_ms", "inference_ms", "cpu_time_ms", "cpu_utilization_percent", "real_time_factor", "characters_per_second", "manual_quality", "error"];
  const quote = (value) => `"${String(value ?? "").replaceAll('"', '""')}"`;
  const rows = state.batchResults.map((item) => headers.map((header) => quote(item[header])).join(","));
  downloadBlob(`\ufeff${headers.join(",")}\n${rows.join("\n")}`, `piper-tts-benchmark-${Date.now()}.csv`, "text/csv;charset=utf-8");
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
$("#load-sample").addEventListener("click", () => loadCases(builtInCases, "已载入内置中文性能测试集。测试将串行执行，以保持 CPU 指标可比较。"));
$("#run-batch").addEventListener("click", runBatch);
$("#export-json").addEventListener("click", exportJson);
$("#export-csv").addEventListener("click", exportCsv);
$("#manifest").addEventListener("change", async (event) => {
  const file = event.target.files[0];
  if (!file) return;
  try {
    const parsed = JSON.parse(await file.text());
    loadCases(Array.isArray(parsed) ? parsed : parsed.cases, `已导入 ${file.name}。测试将串行执行。`);
  } catch (error) {
    setStatus(`导入失败：${error.message}`, true);
  }
});

updateCounter();
updateSliders();
checkHealth();
