(() => {
  const state = { files: [], references: {}, rows: [], running: false };
  const main = document.querySelector("main");
  const status = document.querySelector("#status");
  const statusText = document.querySelector("#statusText");

  main.insertAdjacentHTML("afterbegin", `
    <div class="streaming-notice">
      <div><strong>流式状态：未启用。</strong>当前 Windows GGUF 原生运行时仅支持离线 WAV 推理；本页和导出的报告均使用非流式模式。</div>
    </div>`);
  main.insertAdjacentHTML("beforeend", `
    <section class="panel benchmark-panel" aria-labelledby="benchmarkTitle">
      <div class="head"><h2 id="benchmarkTitle">批量非流式基准测试</h2><span class="subhead">串行执行</span></div>
      <div class="body">
        <div class="benchmark-controls">
          <label class="benchmark-picker">选择一个或多个音频<input id="benchmarkFiles" type="file" accept="audio/*,.wav" multiple></label>
          <label class="benchmark-picker">选择音频文件夹<input id="benchmarkFolder" type="file" accept="audio/*,.wav" webkitdirectory multiple></label>
          <label class="benchmark-picker">导入参考文本 JSON<input id="benchmarkReferences" type="file" accept="application/json,.json"></label>
        </div>
        <p id="benchmarkSelection" class="benchmark-selection">尚未选择批量测试音频。参考文本 JSON 支持 {"文件名.wav":"文本"} 或 samples 数组。</p>
        <div class="benchmark-actions">
          <button id="benchmarkRun" class="button" type="button" disabled>运行批量测试</button>
          <button id="benchmarkCsv" class="button ghost" type="button" disabled>导出 CSV</button>
          <button id="benchmarkJson" class="button ghost" type="button" disabled>导出 JSON</button>
        </div>
        <div class="benchmark-progress" aria-label="批量进度"><span id="benchmarkProgress"></span></div>
        <div class="benchmark-metrics">
          <div class="metric"><span class="metric-label">文件数</span><strong id="benchmarkFilesValue" class="metric-value">0</strong></div>
          <div class="metric"><span class="metric-label">成功数</span><strong id="benchmarkSuccessValue" class="metric-value">0</strong></div>
          <div class="metric"><span class="metric-label">总音频时长</span><strong id="benchmarkDurationValue" class="metric-value">--</strong></div>
          <div class="metric"><span class="metric-label">总推理耗时</span><strong id="benchmarkTimeValue" class="metric-value">--</strong></div>
          <div class="metric"><span class="metric-label">平均 CPU 利用率</span><strong id="benchmarkCpuValue" class="metric-value">--</strong></div>
          <div class="metric"><span class="metric-label">总体字符准确率</span><strong id="benchmarkAccuracyValue" class="metric-value">--</strong></div>
        </div>
        <div class="benchmark-table-wrap">
          <table class="benchmark-table"><thead><tr><th>文件</th><th>时长</th><th>推理耗时</th><th>CPU（整机）</th><th>准确率</th><th>转写文本 / 错误</th></tr></thead><tbody id="benchmarkRows"><tr><td colspan="6" class="benchmark-muted">等待测试。</td></tr></tbody></table>
        </div>
      </div>
    </section>`);

  const ui = {
    files: byId("benchmarkFiles"), folder: byId("benchmarkFolder"), references: byId("benchmarkReferences"), selection: byId("benchmarkSelection"), run: byId("benchmarkRun"), csv: byId("benchmarkCsv"), json: byId("benchmarkJson"), progress: byId("benchmarkProgress"), rows: byId("benchmarkRows"), filesValue: byId("benchmarkFilesValue"), success: byId("benchmarkSuccessValue"), duration: byId("benchmarkDurationValue"), time: byId("benchmarkTimeValue"), cpu: byId("benchmarkCpuValue"), accuracy: byId("benchmarkAccuracyValue")
  };

  function byId(id) { return document.getElementById(id); }
  function setStatus(kind, text) { status.dataset.state = kind; statusText.textContent = text; }
  function seconds(value) { return `${value.toFixed(value < 10 ? 2 : 1)} s`; }
  function milliseconds(value) { return `${Math.round(value)} ms`; }
  function wavName(name) { return name.replace(/\.[^.]+$/, "") + ".wav"; }
  function escapeHtml(value) { const element = document.createElement("div"); element.textContent = value; return element.innerHTML; }

  function uniqueAudioFiles(files) {
    const keys = new Set();
    return [...files]
      .filter(file => file.type.startsWith("audio/") || /\.wav$/i.test(file.name))
      .filter(file => {
        const key = `${file.webkitRelativePath || file.name}:${file.size}`;
        if (keys.has(key)) return false;
        keys.add(key);
        return true;
      });
  }

  function setFiles(files) {
    state.files = uniqueAudioFiles(files);
    state.rows = [];
    ui.progress.style.width = "0%";
    ui.csv.disabled = true;
    ui.json.disabled = true;
    ui.run.disabled = !state.files.length;
    ui.selection.textContent = state.files.length
      ? `已选择 ${state.files.length} 个音频；参考文本：${Object.keys(state.references).length} 条。`
      : "尚未选择批量测试音频。参考文本 JSON 支持 {\"文件名.wav\":\"文本\"} 或 samples 数组。";
    render();
  }

  async function loadReferences(file) {
    try {
      const value = JSON.parse(await file.text());
      const map = {};
      if (Array.isArray(value.samples)) {
        value.samples.forEach(item => { if (item.file && typeof item.text === "string") map[item.file] = item.text; });
      } else if (Array.isArray(value)) {
        value.forEach(item => { if (item.file && typeof item.text === "string") map[item.file] = item.text; });
      } else {
        Object.entries(value).forEach(([key, text]) => { if (typeof text === "string") map[key] = text; });
      }
      state.references = map;
      setFiles(state.files);
    } catch (error) {
      ui.selection.textContent = `参考文本 JSON 无法读取：${error.message}`;
    }
  }

  function findReference(file) {
    return state.references[file.webkitRelativePath] || state.references[file.name] || state.references[wavName(file.name)] || "";
  }

  function encodeWav(buffer) {
    const samples = buffer.getChannelData(0);
    const bytes = new ArrayBuffer(44 + samples.length * 2);
    const view = new DataView(bytes);
    const text = (offset, value) => [...value].forEach((char, index) => view.setUint8(offset + index, char.charCodeAt(0)));
    text(0, "RIFF"); view.setUint32(4, 36 + samples.length * 2, true); text(8, "WAVE"); text(12, "fmt ");
    view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true); view.setUint32(24, 16000, true);
    view.setUint32(28, 32000, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true); text(36, "data"); view.setUint32(40, samples.length * 2, true);
    samples.forEach((sample, index) => {
      const value = Math.max(-1, Math.min(1, sample));
      view.setInt16(44 + index * 2, value < 0 ? value * 32768 : value * 32767, true);
    });
    return new Blob([bytes], { type: "audio/wav" });
  }

  async function convertAudio(file) {
    const context = new (window.AudioContext || window.webkitAudioContext)();
    try {
      const decoded = await context.decodeAudioData(await file.arrayBuffer());
      const OfflineContext = window.OfflineAudioContext || window.webkitOfflineAudioContext;
      const offline = new OfflineContext(1, Math.max(1, Math.ceil(decoded.duration * 16000)), 16000);
      const source = offline.createBufferSource();
      source.buffer = decoded;
      source.connect(offline.destination);
      source.start();
      const mono = await offline.startRendering();
      return { blob: encodeWav(mono), duration: mono.duration };
    } finally {
      await context.close();
    }
  }

  async function transcribe(wav, filename) {
    const form = new FormData();
    form.append("files", wav, filename);
    form.append("lang", "auto");
    const response = await fetch("/api/v1/asr", { method: "POST", body: form });
    const payload = await response.json();
    if (!response.ok) throw new Error(payload.detail || `请求失败 (${response.status})`);
    return payload.result[0];
  }

  function characterMetrics(reference, actual) {
    const expected = [...(reference || "").replace(/\s/g, "")];
    const transcript = [...(actual || "").replace(/\s/g, "")];
    if (!expected.length) return null;
    let previous = Array.from({ length: transcript.length + 1 }, (_, index) => index);
    for (let row = 1; row <= expected.length; row++) {
      const current = [row];
      for (let column = 1; column <= transcript.length; column++) {
        current[column] = expected[row - 1] === transcript[column - 1]
          ? previous[column - 1]
          : 1 + Math.min(previous[column], current[column - 1], previous[column - 1]);
      }
      previous = current;
    }
    return { distance: previous[transcript.length], characters: expected.length, accuracy: Math.max(0, 1 - previous[transcript.length] / expected.length) };
  }

  async function runBenchmark() {
    if (state.running || !state.files.length) return;
    state.running = true;
    state.rows = [];
    ui.run.disabled = true;
    ui.csv.disabled = true;
    ui.json.disabled = true;
    setStatus("busy", "批量测试中");
    render();

    for (let index = 0; index < state.files.length; index++) {
      const file = state.files[index];
      const row = { filename: file.webkitRelativePath || file.name, reference: findReference(file), status: "success" };
      try {
        const converted = await convertAudio(file);
        const result = await transcribe(converted.blob, wavName(file.name));
        const accuracy = characterMetrics(row.reference, result.text);
        Object.assign(row, {
          duration_s: converted.duration,
          inference_ms: result.inference_ms,
          cpu_time_ms: result.cpu_time_ms,
          cpu_utilization_percent: result.cpu_utilization_percent,
          cpu_core_equivalents: result.cpu_core_equivalents,
          processor_count: result.processor_count,
          realtime_speed: converted.duration / (result.inference_ms / 1000),
          text: result.text,
          raw_text: result.raw_text,
          language: result.language,
          emotion: result.emotion,
          event: result.event,
          itn: result.itn,
          accuracy_percent: accuracy ? accuracy.accuracy * 100 : null,
          character_errors: accuracy ? accuracy.distance : null,
          reference_characters: accuracy ? accuracy.characters : null
        });
      } catch (error) {
        Object.assign(row, { status: "error", error: error.message });
      }
      state.rows.push(row);
      ui.progress.style.width = `${(index + 1) / state.files.length * 100}%`;
      render();
    }

    state.running = false;
    ui.run.disabled = false;
    ui.csv.disabled = !state.rows.length;
    ui.json.disabled = !state.rows.length;
    setStatus("ready", "Ready");
  }

  function summary() {
    const success = state.rows.filter(row => row.status === "success");
    const totalDuration = success.reduce((sum, row) => sum + (row.duration_s || 0), 0);
    const totalTime = success.reduce((sum, row) => sum + (row.inference_ms || 0), 0);
    const totalCpuTime = success.reduce((sum, row) => sum + (row.cpu_time_ms || 0), 0);
    const measured = success.filter(row => row.reference_characters);
    const errors = measured.reduce((sum, row) => sum + row.character_errors, 0);
    const characters = measured.reduce((sum, row) => sum + row.reference_characters, 0);
    const processorCount = success[0]?.processor_count || 1;
    return { files: state.files.length, success: success.length, totalDuration, totalTime, processorCount, cpu: totalTime ? totalCpuTime / (totalTime * processorCount) * 100 : null, accuracy: characters ? Math.max(0, 1 - errors / characters) * 100 : null };
  }

  function render() {
    const values = summary();
    ui.filesValue.textContent = values.files;
    ui.success.textContent = values.success;
    ui.duration.textContent = values.success ? seconds(values.totalDuration) : "--";
    ui.time.textContent = values.success ? milliseconds(values.totalTime) : "--";
    ui.cpu.textContent = values.cpu === null ? "--" : `${values.cpu.toFixed(1)}%`;
    ui.accuracy.textContent = values.accuracy === null ? "--" : `${values.accuracy.toFixed(1)}%`;
    ui.rows.innerHTML = "";
    if (!state.rows.length) {
      ui.rows.innerHTML = '<tr><td colspan="6" class="benchmark-muted">等待测试。</td></tr>';
      return;
    }
    state.rows.forEach(row => {
      const accuracy = row.accuracy_percent === null || row.accuracy_percent === undefined ? "--" : `${row.accuracy_percent.toFixed(1)}%`;
      const text = row.status === "success" ? row.text || "" : row.error || "失败";
      const tr = document.createElement("tr");
      tr.innerHTML = `<td>${escapeHtml(row.filename)}</td><td>${row.duration_s ? seconds(row.duration_s) : "--"}</td><td>${row.inference_ms ? milliseconds(row.inference_ms) : "--"}</td><td>${row.cpu_utilization_percent === undefined ? "--" : `${row.cpu_utilization_percent.toFixed(1)}%`}</td><td>${accuracy}</td><td class="${row.status === "success" ? "benchmark-pass" : "benchmark-fail"}">${escapeHtml(text)}</td>`;
      ui.rows.append(tr);
    });
  }

  function report() {
    const values = summary();
    return {
      generated_at: new Date().toISOString(),
      mode: "offline",
      streaming_supported: false,
      runtime: "llama.cpp/GGUF",
      execution: "serial",
      summary: {
        files: values.files,
        successful: values.success,
        total_audio_seconds: Number(values.totalDuration.toFixed(3)),
        total_inference_ms: Number(values.totalTime.toFixed(1)),
        processor_count: values.success ? values.processorCount : null,
        average_cpu_utilization_percent: values.cpu === null ? null : Number(values.cpu.toFixed(1)),
        character_accuracy_percent: values.accuracy === null ? null : Number(values.accuracy.toFixed(1))
      },
      results: state.rows
    };
  }

  function stamp() { return new Date().toISOString().replace(/[:.]/g, "-"); }
  function download(name, type, content) {
    const link = document.createElement("a");
    link.href = URL.createObjectURL(new Blob([content], { type }));
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(link.href), 0);
  }
  function exportJson() { download(`sensevoice-benchmark-${stamp()}.json`, "application/json;charset=utf-8", JSON.stringify(report(), null, 2)); }
  function exportCsv() {
    const header = ["filename", "status", "duration_s", "inference_ms", "cpu_time_ms", "cpu_utilization_percent", "cpu_core_equivalents", "processor_count", "realtime_speed", "accuracy_percent", "reference", "text", "error"];
    const lines = [header, ...state.rows.map(row => header.map(key => row[key] ?? ""))];
    const csv = lines.map(line => line.map(value => `"${String(value).replace(/"/g, '""')}"`).join(",")).join("\r\n");
    download(`sensevoice-benchmark-${stamp()}.csv`, "text/csv;charset=utf-8", "\uFEFF" + csv);
  }

  ui.files.addEventListener("change", event => setFiles(event.target.files));
  ui.folder.addEventListener("change", event => setFiles(event.target.files));
  ui.references.addEventListener("change", event => { if (event.target.files[0]) loadReferences(event.target.files[0]); });
  ui.run.addEventListener("click", runBenchmark);
  ui.csv.addEventListener("click", exportCsv);
  ui.json.addEventListener("click", exportJson);
})();
