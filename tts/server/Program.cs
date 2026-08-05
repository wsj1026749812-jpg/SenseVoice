using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var config = ServiceConfig.FromArgs(args);
config.Validate();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(config.ListenUrl);
var app = builder.Build();
var inferenceGate = new SemaphoreSlim(1, 1);

Directory.CreateDirectory(config.OutputDirectory);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api", () => Results.Ok(new { service = "piper-tts-lite", health = "/health" }));
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    device = "cpu",
    runtime = "Piper/ONNX Runtime",
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
        var result = await NativeRunner.SynthesizeAsync(config, synthesisRequest.Value!, cancellationToken);
        return Results.Ok(result);
    }
    catch (NativeInferenceException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
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

    await inferenceGate.WaitAsync(cancellationToken);
    try
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "audio/L16";
        context.Response.Headers.Append("X-Audio-Sample-Rate", config.SampleRate.ToString());
        context.Response.Headers.Append("X-Audio-Channels", "1");
        context.Response.Headers.Append("Cache-Control", "no-store");
        await NativeRunner.StreamAsync(config, synthesisRequest.Value!, context.Response.Body, cancellationToken);
    }
    catch (NativeInferenceException exception)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await context.Response.WriteAsJsonAsync(new { detail = exception.Message }, cancellationToken);
        }
    }
    finally
    {
        inferenceGate.Release();
    }
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

static class NativeRunner
{
    public static async Task<TtsResult> SynthesizeAsync(ServiceConfig config, TtsInput input, CancellationToken cancellationToken)
    {
        var fileName = $"tts-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.wav";
        var outputPath = Path.Combine(config.OutputDirectory, fileName);
        try
        {
            var process = StartProcess(config, input, outputPath, stream: false);
            var metrics = await RunToCompletionAsync(process, cancellationToken);
            if (!File.Exists(outputPath))
            {
                throw new NativeInferenceException("Piper synthesis completed without producing a WAV file.");
            }

            var audioDurationMs = WavReader.GetDurationMs(outputPath, config.SampleRate);
            return new TtsResult
            {
                Filename = fileName,
                Text = input.Text,
                AudioUrl = $"/audio/{fileName}",
                DownloadUrl = $"/audio/{fileName}",
                SampleRate = config.SampleRate,
                AudioDurationMs = audioDurationMs,
                InferenceMs = metrics.ElapsedMs,
                CpuTimeMs = metrics.CpuTimeMs,
                CpuUtilizationPercent = metrics.CpuUtilizationPercent,
                CpuCoreEquivalents = metrics.CpuCoreEquivalents,
                ProcessorCount = metrics.ProcessorCount,
                RealTimeFactor = Math.Round(metrics.ElapsedMs / Math.Max(audioDurationMs, 1), 4),
                CharactersPerSecond = Math.Round(input.Text.Length / Math.Max(metrics.ElapsedMs / 1000, 0.001), 2),
            };
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }
    }

    public static async Task StreamAsync(ServiceConfig config, TtsInput input, Stream output, CancellationToken cancellationToken)
    {
        using var process = StartProcess(config, input, outputPath: null, stream: true);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        await Task.WhenAll(copyTask, process.WaitForExitAsync(cancellationToken));
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new NativeInferenceException($"Piper streaming synthesis failed: {error.Trim()}");
        }
    }

    private static Process StartProcess(ServiceConfig config, TtsInput input, string? outputPath, bool stream)
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
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(config.RunnerPath);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(config.ModelPath);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(config.ModelConfigPath);
        startInfo.ArgumentList.Add("--length-scale");
        startInfo.ArgumentList.Add(input.LengthScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--noise-scale");
        startInfo.ArgumentList.Add(input.NoiseScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--noise-w-scale");
        startInfo.ArgumentList.Add(input.NoiseWScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (stream)
        {
            startInfo.ArgumentList.Add("--stream");
        }
        else
        {
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputPath!);
        }

        var process = Process.Start(startInfo) ?? throw new NativeInferenceException("Could not start the bundled Piper runtime.");
        process.StandardInput.Write(input.Text);
        process.StandardInput.Close();
        return process;
    }

    private static async Task<ProcessMetrics> RunToCompletionAsync(Process process, CancellationToken cancellationToken)
    {
        using (process)
        {
            var stopwatch = Stopwatch.StartNew();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var standardOutput = await outputTask;
            var standardError = await errorTask;
            stopwatch.Stop();
            var cpuTimeMs = process.TotalProcessorTime.TotalMilliseconds;

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
                throw new NativeInferenceException($"Piper synthesis failed: {detail.Trim()}");
            }

            var elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1);
            var cpuCoreEquivalents = cpuTimeMs / Math.Max(stopwatch.Elapsed.TotalMilliseconds, 1);
            return new ProcessMetrics(
                elapsedMs,
                Math.Round(cpuTimeMs, 1),
                Math.Round(cpuCoreEquivalents / Environment.ProcessorCount * 100, 1),
                Math.Round(cpuCoreEquivalents, 2),
                Environment.ProcessorCount);
        }
    }
}

sealed record ProcessMetrics(
    double ElapsedMs,
    double CpuTimeMs,
    double CpuUtilizationPercent,
    double CpuCoreEquivalents,
    int ProcessorCount);

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

    [JsonPropertyName("real_time_factor")]
    public required double RealTimeFactor { get; init; }

    [JsonPropertyName("characters_per_second")]
    public required double CharactersPerSecond { get; init; }
}
