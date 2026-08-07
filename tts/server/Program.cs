using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var config = ServiceConfig.FromArgs(args);
config.Validate();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(config.ListenUrl);
var app = builder.Build();
var inferenceGate = new SemaphoreSlim(1, 1);
var streamMetrics = new ConcurrentDictionary<string, StreamMetricState>();

Directory.CreateDirectory(config.OutputDirectory);
var worker = await TtsWorker.StartAsync(config, CancellationToken.None);
app.Lifetime.ApplicationStopping.Register(worker.Dispose);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api", () => Results.Ok(new { service = "piper-tts-lite", health = "/health" }));
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    device = "cpu",
    runtime = "Piper/ONNX Runtime",
    worker = new
    {
        persistent = true,
        process_id = worker.ProcessId,
    },
    streaming = new
    {
        supported = true,
        endpoint = "/api/v1/tts/stream",
        format = $"pcm_s16le;rate={config.SampleRate};channels=1",
    },
    voice = new
    {
        id = "zh_CN-huayan-medium",
        model = Path.GetFileName(config.ModelPath),
        sample_rate = config.SampleRate,
        language = "zh_CN",
    },
}));
app.MapGet("/api/v1/voices", () => Results.Ok(new
{
    voices = new[]
    {
        new
        {
            id = "zh_CN-huayan-medium",
            language = "zh_CN",
            sample_rate = config.SampleRate,
            speakers = 1,
        },
    },
}));

app.MapPost("/api/v1/tts", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    var synthesisRequest = await TtsInput.ReadAsync(request, cancellationToken);
    if (synthesisRequest.Error is not null)
    {
        return Results.UnprocessableEntity(new { detail = synthesisRequest.Error });
    }

    CleanupExpiredAudio(config.OutputDirectory);
    await inferenceGate.WaitAsync(cancellationToken);
    try
    {
        var result = await worker.SynthesizeAsync(synthesisRequest.Value!, cancellationToken);
        return Results.Ok(result);
    }
    catch (NativeInferenceException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
    catch (Exception exception)
    {
        return Results.Json(new { detail = $"TTS request failed: {exception.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
    }
    finally
    {
        inferenceGate.Release();
    }
});

app.MapPost("/api/v1/tts/stream", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var synthesisRequest = await TtsInput.ReadAsync(context.Request, cancellationToken);
    if (synthesisRequest.Error is not null)
    {
        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        await context.Response.WriteAsJsonAsync(new { detail = synthesisRequest.Error }, cancellationToken);
        return;
    }

    var requestId = Guid.NewGuid().ToString("N");
    streamMetrics[requestId] = new StreamMetricState(DateTime.UtcNow, null, null);
    foreach (var stale in streamMetrics.Where(item => item.Value.CreatedAt < DateTime.UtcNow.AddHours(-1)).ToArray())
    {
        streamMetrics.TryRemove(stale.Key, out _);
    }

    await inferenceGate.WaitAsync(cancellationToken);
    try
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "audio/L16";
        context.Response.Headers.Append("X-Audio-Sample-Rate", config.SampleRate.ToString());
        context.Response.Headers.Append("X-Audio-Channels", "1");
        context.Response.Headers.Append("X-Stream-Request-Id", requestId);
        context.Response.Headers.Append("Access-Control-Expose-Headers", "X-Audio-Sample-Rate, X-Audio-Channels, X-Stream-Request-Id");
        context.Response.Headers.Append("Cache-Control", "no-store");
        var metrics = await worker.StreamAsync(synthesisRequest.Value!, context.Response.Body, cancellationToken);
        streamMetrics[requestId] = new StreamMetricState(DateTime.UtcNow, metrics, null);
    }
    catch (NativeInferenceException exception)
    {
        streamMetrics[requestId] = new StreamMetricState(DateTime.UtcNow, null, exception.Message);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await context.Response.WriteAsJsonAsync(new { detail = exception.Message }, cancellationToken);
        }
    }
    catch (Exception exception)
    {
        streamMetrics[requestId] = new StreamMetricState(DateTime.UtcNow, null, exception.Message);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { detail = $"TTS streaming failed: {exception.Message}" }, cancellationToken);
        }
    }
    finally
    {
        inferenceGate.Release();
    }
});

app.MapGet("/api/v1/tts/stream/metrics/{requestId}", (string requestId) =>
{
    if (!streamMetrics.TryGetValue(requestId, out var state))
    {
        return Results.NotFound(new { detail = "Unknown or expired stream request ID." });
    }
    if (state.Error is not null)
    {
        return Results.Json(new { status = "error", detail = state.Error }, statusCode: StatusCodes.Status500InternalServerError);
    }
    return state.Metrics is null
        ? Results.Json(new { status = "pending" }, statusCode: StatusCodes.Status202Accepted)
        : Results.Ok(new { status = "complete", metrics = state.Metrics });
});

app.MapGet("/audio/{fileName}", (string fileName) =>
{
    if (!IsSafeAudioName(fileName))
    {
        return Results.NotFound();
    }

    var path = Path.Combine(config.OutputDirectory, fileName);
    return File.Exists(path)
        ? Results.File(path, "audio/wav", enableRangeProcessing: true)
        : Results.NotFound();
});

app.Run();

static bool IsSafeAudioName(string fileName) =>
    fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
    fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
    !fileName.Contains(Path.DirectorySeparatorChar) &&
    !fileName.Contains(Path.AltDirectorySeparatorChar);

static void CleanupExpiredAudio(string outputDirectory)
{
    var cutoff = DateTime.UtcNow.AddHours(-24);
    foreach (var file in Directory.EnumerateFiles(outputDirectory, "*.wav"))
    {
        if (File.GetLastWriteTimeUtc(file) < cutoff)
        {
            File.Delete(file);
        }
    }
}

sealed record StreamMetricState(DateTime CreatedAt, ProcessMetrics? Metrics, string? Error);

sealed record ServiceConfig(
    string ListenUrl,
    string PythonPath,
    string RunnerPath,
    string ModelPath,
    string ModelConfigPath,
    string OutputDirectory,
    int SampleRate)
{
    public static ServiceConfig FromArgs(string[] args)
    {
        var root = AppContext.BaseDirectory;
        var listenUrl = "http://127.0.0.1:50100";
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--url" && index + 1 < args.Length)
            {
                listenUrl = args[++index];
            }
        }

        return new ServiceConfig(
            listenUrl,
            Path.Combine(root, "runtime", "python", "python.exe"),
            Path.Combine(root, "runtime", "run_tts.py"),
            Path.Combine(root, "models", "zh_CN-huayan-medium.onnx"),
            Path.Combine(root, "models", "zh_CN-huayan-medium.onnx.json"),
            Path.Combine(root, "output"),
            22050);
    }

    public void Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("--url must be an absolute http:// URL.");
        }
        foreach (var path in new[] { PythonPath, RunnerPath, ModelPath, ModelConfigPath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Required Piper TTS Lite file was not found.", path);
            }
        }
    }
}

sealed record TtsInput(string Text, double LengthScale, double NoiseScale, double NoiseWScale)
{
    public static async Task<(TtsInput? Value, string? Error)> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return (null, "Use application/json with a text field.");
        }

        TtsApiRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(request.Body, AppJsonContext.Default.TtsApiRequest, cancellationToken);
        }
        catch
        {
            return (null, "Invalid JSON request body.");
        }

        var text = body?.Text?.Trim();
        if (body is null || string.IsNullOrWhiteSpace(text))
        {
            return (null, "text is required.");
        }
        if (text.Length > 2000)
        {
            return (null, "text must be 2000 characters or fewer.");
        }

        var lengthScale = body.LengthScale ?? 1.0;
        var noiseScale = body.NoiseScale ?? 0.667;
        var noiseWScale = body.NoiseWScale ?? 0.8;
        if (lengthScale is < 0.5 or > 2.0 || noiseScale is < 0 or > 1.5 || noiseWScale is < 0 or > 1.5)
        {
            return (null, "length_scale must be 0.5-2.0; noise_scale and noise_w_scale must be 0-1.5.");
        }

        return (new TtsInput(text, lengthScale, noiseScale, noiseWScale), null);
    }
}

sealed class TtsApiRequest
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("length_scale")]
    public double? LengthScale { get; init; }

    [JsonPropertyName("noise_scale")]
    public double? NoiseScale { get; init; }

    [JsonPropertyName("noise_w_scale")]
    public double? NoiseWScale { get; init; }
}

[JsonSerializable(typeof(TtsApiRequest))]
internal partial class AppJsonContext : JsonSerializerContext;

sealed class TtsWorker : IDisposable
{
    private readonly ServiceConfig config;
    private readonly Process process;
    private readonly Stream input;
    private readonly Stream output;
    private readonly Task<string> standardErrorTask;
    private int disposed;

    private TtsWorker(ServiceConfig config, Process process)
    {
        this.config = config;
        this.process = process;
        input = process.StandardInput.BaseStream;
        output = process.StandardOutput.BaseStream;
        standardErrorTask = process.StandardError.ReadToEndAsync();
    }

    public int ProcessId => process.Id;

    public static async Task<TtsWorker> StartAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.PythonPath,
            WorkingDirectory = Path.GetDirectoryName(config.RunnerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(config.RunnerPath);
        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(config.ModelPath);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(config.ModelConfigPath);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        var process = Process.Start(startInfo) ?? throw new NativeInferenceException("Could not start the persistent Piper worker.");
        var worker = new TtsWorker(config, process);
        try
        {
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            var ready = await worker.ReadResponseAsync(startupTimeout.Token);
            EnsureSuccess(ready, expectedStatus: "ready");
            await worker.WarmUpAsync(startupTimeout.Token);
            return worker;
        }
        catch
        {
            worker.Dispose();
            throw;
        }
    }

    public async Task<TtsResult> SynthesizeAsync(TtsInput request, CancellationToken cancellationToken)
    {
        EnsureAlive();
        var fileName = $"tts-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.wav";
        var outputPath = Path.Combine(config.OutputDirectory, fileName);
        try
        {
            var metrics = await MeasureAsync(async () =>
            {
                await WriteCommandAsync(WorkerCommand.Wav(request, outputPath), CancellationToken.None);
                EnsureSuccess(await ReadResponseAsync(CancellationToken.None));
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(outputPath))
            {
                throw new NativeInferenceException("Piper worker completed without producing a WAV file.");
            }

            var audioDurationMs = WavReader.GetDurationMs(outputPath, config.SampleRate);
            return new TtsResult
            {
                Filename = fileName,
                Text = request.Text,
                AudioUrl = $"/audio/{fileName}",
                DownloadUrl = $"/audio/{fileName}",
                SampleRate = config.SampleRate,
                AudioDurationMs = audioDurationMs,
                InferenceMs = metrics.ElapsedMs,
                CpuTimeMs = metrics.CpuTimeMs,
                CpuUtilizationPercent = metrics.CpuUtilizationPercent,
                CpuCoreEquivalents = metrics.CpuCoreEquivalents,
                ProcessorCount = metrics.ProcessorCount,
                PeakWorkingSetMb = metrics.PeakWorkingSetMb,
                TotalPhysicalMemoryMb = metrics.TotalPhysicalMemoryMb,
                MemoryUtilizationPercent = metrics.MemoryUtilizationPercent,
                RealTimeFactor = Math.Round(metrics.ElapsedMs / Math.Max(audioDurationMs, 1), 4),
                CharactersPerSecond = Math.Round(request.Text.Length / Math.Max(metrics.ElapsedMs / 1000, 0.001), 2),
            };
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }
    }

    public async Task<ProcessMetrics> StreamAsync(TtsInput request, Stream destination, CancellationToken cancellationToken)
    {
        EnsureAlive();
        return await MeasureAsync(async () =>
        {
            await WriteCommandAsync(WorkerCommand.Stream(request), CancellationToken.None);
            var discardOutput = false;
            while (true)
            {
                var length = await ReadUInt32Async(output, CancellationToken.None);
                if (length == 0) break;
                if (length > 16 * 1024 * 1024)
                {
                    throw new NativeInferenceException("Persistent Piper worker returned an invalid stream frame.");
                }
                var payload = new byte[length];
                await ReadExactlyAsync(output, payload, CancellationToken.None);
                if (!discardOutput)
                {
                    try
                    {
                        await destination.WriteAsync(payload, cancellationToken);
                        await destination.FlushAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        discardOutput = true;
                    }
                    catch (IOException)
                    {
                        discardOutput = true;
                    }
                }
            }
            EnsureSuccess(await ReadResponseAsync(CancellationToken.None));
        });
    }

    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        var warmupPath = Path.Combine(config.OutputDirectory, $"warmup-{Guid.NewGuid():N}.wav");
        try
        {
            var request = new TtsInput("预热。", 1.0, 0.667, 0.8);
            await WriteCommandAsync(WorkerCommand.Wav(request, warmupPath), cancellationToken);
            EnsureSuccess(await ReadResponseAsync(cancellationToken));
        }
        finally
        {
            File.Delete(warmupPath);
        }
    }

    private async Task<ProcessMetrics> MeasureAsync(Func<Task> operation)
    {
        EnsureAlive();
        process.Refresh();
        var cpuStartMs = process.TotalProcessorTime.TotalMilliseconds;
        using var sampling = new CancellationTokenSource();
        var peakTask = ProcessMemory.TrackPeakWorkingSetAsync(process, sampling.Token);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await operation();
        }
        finally
        {
            stopwatch.Stop();
            sampling.Cancel();
        }
        var peakWorkingSetBytes = await peakTask;
        process.Refresh();
        var cpuTimeMs = Math.Max(0, process.TotalProcessorTime.TotalMilliseconds - cpuStartMs);
        return CreateMetrics(stopwatch.Elapsed.TotalMilliseconds, cpuTimeMs, peakWorkingSetBytes);
    }

    private async Task WriteCommandAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(command);
        await input.WriteAsync(payload, cancellationToken);
        await input.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
        await input.FlushAsync(cancellationToken);
    }

    private async Task<JsonDocument> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(output, cancellationToken);
        if (line.Length == 0)
        {
            var error = standardErrorTask.IsCompletedSuccessfully ? standardErrorTask.Result.Trim() : "worker output ended unexpectedly";
            throw new NativeInferenceException($"Persistent Piper worker stopped: {error}");
        }
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new NativeInferenceException($"Persistent Piper worker returned invalid JSON: {exception.Message}");
        }
    }

    private static void EnsureSuccess(JsonDocument response, string expectedStatus = "ok")
    {
        using (response)
        {
            var root = response.RootElement;
            var status = root.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            if (string.Equals(status, expectedStatus, StringComparison.Ordinal)) return;
            var detail = root.TryGetProperty("detail", out var detailValue) ? detailValue.GetString() : null;
            throw new NativeInferenceException(detail ?? $"Persistent Piper worker returned status '{status ?? "unknown"}'.");
        }
    }

    private void EnsureAlive()
    {
        if (Volatile.Read(ref disposed) != 0 || process.HasExited)
        {
            var error = standardErrorTask.IsCompletedSuccessfully ? standardErrorTask.Result.Trim() : "process is not running";
            throw new NativeInferenceException($"Persistent Piper worker is unavailable: {error}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            input.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort during service shutdown.
        }
        process.Dispose();
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken);
            if (read == 0) return line.ToArray();
            if (oneByte[0] == (byte)'\n') return line.ToArray();
            if (oneByte[0] != (byte)'\r') line.WriteByte(oneByte[0]);
            if (line.Length > 64 * 1024) throw new NativeInferenceException("Persistent Piper worker response was too large.");
        }
    }

    private static async Task<uint> ReadUInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        await ReadExactlyAsync(stream, buffer, cancellationToken);
        return BitConverter.ToUInt32(buffer, 0);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) throw new NativeInferenceException("Persistent Piper worker output ended unexpectedly.");
            offset += read;
        }
    }

    private static ProcessMetrics CreateMetrics(double elapsedMs, double cpuTimeMs, long peakWorkingSetBytes)
    {
        var totalPhysicalMemoryBytes = SystemMemory.GetTotalPhysicalMemoryBytes();
        var cpuCoreEquivalents = cpuTimeMs / Math.Max(elapsedMs, 1);
        return new ProcessMetrics(
            Math.Round(elapsedMs, 1),
            Math.Round(cpuTimeMs, 1),
            Math.Round(cpuCoreEquivalents / Environment.ProcessorCount * 100, 1),
            Math.Round(cpuCoreEquivalents, 2),
            Environment.ProcessorCount,
            Math.Round(peakWorkingSetBytes / 1024d / 1024d, 1),
            Math.Round(totalPhysicalMemoryBytes / 1024d / 1024d, 1),
            Math.Round(peakWorkingSetBytes / (double)Math.Max(totalPhysicalMemoryBytes, 1) * 100, 2));
    }
}

sealed record WorkerCommand(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("length_scale")] double LengthScale,
    [property: JsonPropertyName("noise_scale")] double NoiseScale,
    [property: JsonPropertyName("noise_w_scale")] double NoiseWScale,
    [property: JsonPropertyName("output")] string? Output)
{
    public static WorkerCommand Wav(TtsInput input, string output) =>
        new("wav", input.Text, input.LengthScale, input.NoiseScale, input.NoiseWScale, output);

    public static WorkerCommand Stream(TtsInput input) =>
        new("stream", input.Text, input.LengthScale, input.NoiseScale, input.NoiseWScale, null);
}

sealed record ProcessMetrics(
    double ElapsedMs,
    double CpuTimeMs,
    double CpuUtilizationPercent,
    double CpuCoreEquivalents,
    int ProcessorCount,
    double PeakWorkingSetMb,
    double TotalPhysicalMemoryMb,
    double MemoryUtilizationPercent);

static class SystemMemory
{
    public static long GetTotalPhysicalMemoryBytes()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysical <= long.MaxValue)
            {
                return (long)status.TotalPhysical;
            }
        }
        catch
        {
            // Keep inference successful even when Windows memory metadata is unavailable.
        }
        return Math.Max(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 1);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

static class ProcessMemory
{
    public static async Task<long> TrackPeakWorkingSetAsync(Process process, CancellationToken cancellationToken)
    {
        long peak = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (process.HasExited) return peak;
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
            }
            catch (InvalidOperationException)
            {
                return peak;
            }
            try
            {
                await Task.Delay(25, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return peak;
    }
}

static class WavReader
{
    public static double GetDurationMs(string path, int fallbackSampleRate)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new NativeInferenceException("Piper produced an invalid WAV file.");
        }
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new NativeInferenceException("Piper produced an invalid WAV file.");
        }

        var byteRate = fallbackSampleRate * 2;
        long dataLength = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkLength = reader.ReadInt32();
            if (chunkLength < 0 || stream.Position + chunkLength > stream.Length)
            {
                throw new NativeInferenceException("Piper produced an invalid WAV file.");
            }
            if (chunkId == "fmt ")
            {
                reader.ReadInt16();
                reader.ReadInt16();
                reader.ReadInt32();
                byteRate = reader.ReadInt32();
                stream.Position += chunkLength - 12;
            }
            else if (chunkId == "data")
            {
                dataLength = chunkLength;
                break;
            }
            else
            {
                stream.Position += chunkLength;
            }
            if (chunkLength % 2 == 1)
            {
                stream.Position++;
            }
        }
        return Math.Round(dataLength * 1000d / Math.Max(byteRate, 1), 1);
    }
}

sealed class NativeInferenceException(string message) : Exception(message);

sealed class TtsResult
{
    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("audio_url")]
    public required string AudioUrl { get; init; }

    [JsonPropertyName("download_url")]
    public required string DownloadUrl { get; init; }

    [JsonPropertyName("sample_rate")]
    public required int SampleRate { get; init; }

    [JsonPropertyName("audio_duration_ms")]
    public required double AudioDurationMs { get; init; }

    [JsonPropertyName("inference_ms")]
    public required double InferenceMs { get; init; }

    [JsonPropertyName("cpu_time_ms")]
    public required double CpuTimeMs { get; init; }

    [JsonPropertyName("cpu_utilization_percent")]
    public required double CpuUtilizationPercent { get; init; }

    [JsonPropertyName("cpu_core_equivalents")]
    public required double CpuCoreEquivalents { get; init; }

    [JsonPropertyName("processor_count")]
    public required int ProcessorCount { get; init; }

    [JsonPropertyName("peak_working_set_mb")]
    public required double PeakWorkingSetMb { get; init; }

    [JsonPropertyName("total_physical_memory_mb")]
    public required double TotalPhysicalMemoryMb { get; init; }

    [JsonPropertyName("memory_utilization_percent")]
    public required double MemoryUtilizationPercent { get; init; }

    [JsonPropertyName("real_time_factor")]
    public required double RealTimeFactor { get; init; }

    [JsonPropertyName("characters_per_second")]
    public required double CharactersPerSecond { get; init; }
}
