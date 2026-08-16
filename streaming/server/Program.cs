using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SherpaOnnx;

var config = ServiceConfig.FromArgs(args);
config.Validate();

using var service = new StreamingAsrService(config);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(config.ListenUrl);
var app = builder.Build();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api", () => Results.Ok(new { service = "sherpa-streaming-asr", health = "/health", websocket = "/api/v1/asr/stream" }));
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    device = "cpu",
    runtime = "sherpa-onnx",
    streaming = new { supported = true, transport = "websocket", sample_rate = 16000, format = "pcm_s16le" },
    hotwords = new { supported = true, decoding_method = "modified_beam_search", modeling_unit = "cjkchar" },
    model = config.ModelName,
    service_metrics = SystemMetrics.Capture(),
}));

app.MapPost("/api/v1/asr", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { detail = "Use multipart/form-data with a WAV file in the audio field." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var audio = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
    if (audio is null || audio.Length == 0)
    {
        return Results.UnprocessableEntity(new { detail = "Provide one non-empty WAV file in the audio field." });
    }

    try
    {
        await using var input = audio.OpenReadStream();
        var samples = Pcm16Wav.Read(input);
        var hotwords = HotwordCodec.Parse(form["hotwords"].FirstOrDefault());
        var hotwordScore = HotwordCodec.ParseScore(form["hotword_score"].FirstOrDefault());
        if (Math.Abs(hotwordScore - StreamingAsrService.HotwordScore) > 0.001F)
        {
            return Results.UnprocessableEntity(new { detail = $"This package uses a fixed hotword_score of {StreamingAsrService.HotwordScore:0.0}." });
        }
        await using var session = await service.CreateSessionAsync(hotwords, cancellationToken);
        var finals = new List<string>();
        var update = await session.AcceptAsync(samples, cancellationToken);
        if (update.EndpointText is { Length: > 0 }) finals.Add(update.EndpointText);
        var completion = await session.CompleteAsync(cancellationToken);
        if (completion.Text is { Length: > 0 }) finals.Add(completion.Text);

        return Results.Ok(new
        {
            filename = audio.FileName,
            text = string.Concat(finals),
            segments = finals,
            hotwords,
            hotword_score = hotwordScore,
            metrics = completion.Metrics,
        });
    }
    catch (InvalidDataException)
    {
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }
    catch (ArgumentException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
});

app.Map("/api/v1/asr/stream", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { detail = "Use a WebSocket connection." });
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await StreamSocket.HandleAsync(socket, service, context.RequestAborted);
});

app.Run();

sealed record ServiceConfig(string ListenUrl, string ModelDirectory, string ModelName, int NumThreads)
{
    public static ServiceConfig FromArgs(string[] args)
    {
        var root = AppContext.BaseDirectory;
        var listenUrl = "http://127.0.0.1:50200";
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--url" && index + 1 < args.Length)
            {
                listenUrl = args[++index];
            }
        }

        return new ServiceConfig(
            listenUrl,
            Path.Combine(root, "models", "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23"),
            "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23",
            Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
    }

    public void Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("--url must be an absolute http:// URL.");
        }

        foreach (var file in ModelFiles.All(ModelDirectory))
        {
            if (!File.Exists(file)) throw new FileNotFoundException("Required streaming ASR model file was not found.", file);
        }
    }
}

static class ModelFiles
{
    public static IEnumerable<string> All(string directory)
    {
        yield return Path.Combine(directory, "encoder-epoch-99-avg-1.int8.onnx");
        yield return Path.Combine(directory, "decoder-epoch-99-avg-1.int8.onnx");
        yield return Path.Combine(directory, "joiner-epoch-99-avg-1.int8.onnx");
        yield return Path.Combine(directory, "tokens.txt");
    }
}

sealed class StreamingAsrService : IDisposable
{
    private readonly SemaphoreSlim decodeGate = new(1, 1);
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private readonly ServiceConfig config;
    private OnlineRecognizer? recognizer;
    private string? recognizerKey;
    public const float HotwordScore = 2F;

    public StreamingAsrService(ServiceConfig config)
    {
        this.config = config;
    }

    public async Task<StreamingSession> CreateSessionAsync(IReadOnlyList<string> hotwords, CancellationToken cancellationToken)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            var key = string.Join("\n", hotwords);
            if (recognizer is null || !string.Equals(recognizerKey, key, StringComparison.Ordinal))
            {
                recognizer?.Dispose();
                recognizer = CreateRecognizer(hotwords);
                recognizerKey = key;
            }
            return new StreamingSession(this, recognizer.CreateStream(), hotwords, HotwordScore);
        }
        catch
        {
            sessionGate.Release();
            throw;
        }
    }

    private OnlineRecognizer CreateRecognizer(IReadOnlyList<string> hotwords)
    {
        var model = config.ModelDirectory;
        var recognizerConfig = new OnlineRecognizerConfig();
        recognizerConfig.FeatConfig.SampleRate = 16000;
        recognizerConfig.FeatConfig.FeatureDim = 80;
        recognizerConfig.ModelConfig.Transducer.Encoder = Path.Combine(model, "encoder-epoch-99-avg-1.int8.onnx");
        recognizerConfig.ModelConfig.Transducer.Decoder = Path.Combine(model, "decoder-epoch-99-avg-1.int8.onnx");
        recognizerConfig.ModelConfig.Transducer.Joiner = Path.Combine(model, "joiner-epoch-99-avg-1.int8.onnx");
        recognizerConfig.ModelConfig.Tokens = Path.Combine(model, "tokens.txt");
        recognizerConfig.ModelConfig.Provider = "cpu";
        recognizerConfig.ModelConfig.NumThreads = config.NumThreads;
        recognizerConfig.ModelConfig.ModelingUnit = "cjkchar";
        recognizerConfig.DecodingMethod = "modified_beam_search";
        recognizerConfig.MaxActivePaths = 4;
        recognizerConfig.HotwordsScore = HotwordScore;
        recognizerConfig.EnableEndpoint = 1;
        recognizerConfig.Rule1MinTrailingSilence = 2.4F;
        recognizerConfig.Rule2MinTrailingSilence = 1.0F;
        recognizerConfig.Rule3MinUtteranceLength = 20F;
        if (hotwords.Count > 0)
        {
            var directory = Path.Combine(Path.GetTempPath(), "sherpa-streaming-asr-hotwords");
            Directory.CreateDirectory(directory);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", hotwords)))).ToLowerInvariant();
            var path = Path.Combine(directory, $"{hash}.txt");
            File.WriteAllLines(path, hotwords, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            recognizerConfig.HotwordsFile = path;
        }
        return new OnlineRecognizer(recognizerConfig);
    }

    internal async Task DecodeAvailableAsync(OnlineStream stream, CancellationToken cancellationToken)
    {
        while (recognizer!.IsReady(stream))
        {
            await decodeGate.WaitAsync(cancellationToken);
            try
            {
                recognizer.Decode(stream);
            }
            finally
            {
                decodeGate.Release();
            }
        }
    }

    internal string GetText(OnlineStream stream) => recognizer!.GetResult(stream).Text.Trim();
    internal bool IsEndpoint(OnlineStream stream) => recognizer!.IsEndpoint(stream);
    internal void Reset(OnlineStream stream) => recognizer!.Reset(stream);

    public void Dispose()
    {
        recognizer?.Dispose();
        decodeGate.Dispose();
        sessionGate.Dispose();
    }

    internal void ReleaseSession() => sessionGate.Release();
}

sealed class StreamingSession : IAsyncDisposable
{
    private readonly StreamingAsrService service;
    private readonly OnlineStream stream;
    private readonly TimeSpan processCpuStart;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private string lastPartial = string.Empty;
    private bool completed;

    public StreamingSession(StreamingAsrService service, OnlineStream stream, IReadOnlyList<string> hotwords, float hotwordScore)
    {
        this.service = service;
        this.stream = stream;
        Hotwords = hotwords;
        HotwordScore = hotwordScore;
        using var process = Process.GetCurrentProcess();
        processCpuStart = process.TotalProcessorTime;
    }

    public IReadOnlyList<string> Hotwords { get; }
    public float HotwordScore { get; }

    public async Task<DecodeUpdate> AcceptAsync(float[] samples, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        if (samples.Length == 0) return DecodeUpdate.Empty;

        stream.AcceptWaveform(16000, samples);
        await service.DecodeAvailableAsync(stream, cancellationToken);
        var text = service.GetText(stream);
        var changed = !string.Equals(text, lastPartial, StringComparison.Ordinal);
        lastPartial = text;

        if (service.IsEndpoint(stream))
        {
            var endpointText = text;
            service.Reset(stream);
            lastPartial = string.Empty;
            return new DecodeUpdate(changed ? text : null, endpointText);
        }

        return new DecodeUpdate(changed ? text : null, null);
    }

    public async Task<DecodeCompletion> CompleteAsync(CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        completed = true;
        stream.InputFinished();
        await service.DecodeAvailableAsync(stream, cancellationToken);
        stopwatch.Stop();
        using var process = Process.GetCurrentProcess();
        var cpuMs = (process.TotalProcessorTime - processCpuStart).TotalMilliseconds;
        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        var cores = cpuMs / Math.Max(elapsedMs, 1);
        return new DecodeCompletion(
            service.GetText(stream),
            new SessionMetrics(
                Math.Round(elapsedMs, 1),
                Math.Round(cpuMs, 1),
                Math.Round(cores / Environment.ProcessorCount * 100, 1),
                Math.Round(cores, 2),
                Environment.ProcessorCount,
                SystemMetrics.Capture()));
    }

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        service.ReleaseSession();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfCompleted()
    {
        if (completed) throw new InvalidOperationException("The streaming session has already completed.");
    }
}

sealed record DecodeUpdate(string? PartialText, string? EndpointText)
{
    public static readonly DecodeUpdate Empty = new(null, null);
}

sealed record DecodeCompletion(string Text, SessionMetrics Metrics);

sealed record SessionMetrics(
    double ElapsedMs,
    double CpuTimeMs,
    double CpuUtilizationPercent,
    double CpuCoreEquivalents,
    int ProcessorCount,
    SystemMetricSnapshot ServiceMetrics);

static class StreamSocket
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(WebSocket socket, StreamingAsrService service, CancellationToken cancellationToken)
    {
        try
        {
            var start = await ReceiveTextAsync(socket, cancellationToken);
            var options = StreamOptions.FromJson(start);
            await using var session = await service.CreateSessionAsync(options.Hotwords, cancellationToken);
            await SendAsync(socket, new { type = "ready", sample_rate = 16000, hotwords = session.Hotwords, hotword_score = session.HotwordScore }, cancellationToken);

            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(socket, cancellationToken);
                if (message.Type == WebSocketMessageType.Close) break;
                if (message.Type == WebSocketMessageType.Text)
                {
                    using var control = JsonDocument.Parse(message.Payload);
                    if (control.RootElement.TryGetProperty("type", out var type) && type.GetString() == "stop") break;
                    continue;
                }
                if (message.Type != WebSocketMessageType.Binary) continue;
                if (message.Payload.Length % 2 != 0) throw new ArgumentException("PCM audio must be 16-bit little-endian samples.");

                var update = await session.AcceptAsync(Pcm16.ToFloats(message.Payload), cancellationToken);
                if (update.PartialText is { Length: > 0 })
                {
                    await SendAsync(socket, new { type = "partial", text = update.PartialText }, cancellationToken);
                }
                if (update.EndpointText is { Length: > 0 })
                {
                    await SendAsync(socket, new { type = "final", text = update.EndpointText, reason = "endpoint" }, cancellationToken);
                }
            }

            var completion = await session.CompleteAsync(CancellationToken.None);
            await SendAsync(socket, new { type = "complete", text = completion.Text, metrics = completion.Metrics }, CancellationToken.None);
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "completed", CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "request cancelled", CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (socket.State == WebSocketState.Open)
            {
                await SendAsync(socket, new { type = "error", detail = exception.Message }, CancellationToken.None);
                await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "error", CancellationToken.None);
            }
        }
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var message = await ReceiveMessageAsync(socket, cancellationToken);
        if (message.Type != WebSocketMessageType.Text) throw new ArgumentException("Send a JSON start message before audio data.");
        return Encoding.UTF8.GetString(message.Payload);
    }

    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        await using var payload = new MemoryStream();
        WebSocketMessageType? messageType = null;
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return (WebSocketMessageType.Close, []);
            messageType ??= result.MessageType;
            if (messageType != result.MessageType) throw new ArgumentException("WebSocket message fragments must use one type.");
            if (payload.Length + result.Count > MaxMessageBytes) throw new ArgumentException("WebSocket message is too large.");
            await payload.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (result.EndOfMessage) return (messageType.Value, payload.ToArray());
        }
    }

    private static Task SendAsync(WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, cancellationToken);
    }
}

sealed record StreamOptions(IReadOnlyList<string> Hotwords, float HotwordScore)
{
    public static StreamOptions FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "start", StringComparison.Ordinal))
        {
            throw new ArgumentException("The first WebSocket message must be { type: 'start' }.");
        }

        var hotwords = root.TryGetProperty("hotwords", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];
        var score = root.TryGetProperty("hotword_score", out var rawScore) && rawScore.TryGetSingle(out var value)
            ? HotwordCodec.ParseScore(value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : StreamingAsrService.HotwordScore;
        if (Math.Abs(score - StreamingAsrService.HotwordScore) > 0.001F)
        {
            throw new ArgumentException($"This package uses a fixed hotword_score of {StreamingAsrService.HotwordScore:0.0}.");
        }
        return new StreamOptions(HotwordCodec.Normalize(hotwords), StreamingAsrService.HotwordScore);
    }
}

static class HotwordCodec
{
    public static IReadOnlyList<string> Parse(string? value) => Normalize((value ?? string.Empty).Split(new[] { "\r\n", "\n", ",", "，" }, StringSplitOptions.RemoveEmptyEntries));

    public static IReadOnlyList<string> Normalize(IEnumerable<string> values) => values
        .Select(value => value.Trim())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .Take(50)
        .ToArray();

    public static float ParseScore(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 2F;
        if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score) || score is < 0.1F or > 10F)
        {
            throw new ArgumentException("hotword_score must be a number between 0.1 and 10.");
        }
        return score;
    }

    public static string Encode(IEnumerable<string> values) => string.Join("/", values.Select(value => string.Join(' ', value.EnumerateRunes().Where(rune => !Rune.IsWhiteSpace(rune)).Select(rune => rune.ToString()))));
}

static class Pcm16
{
    public static float[] ToFloats(byte[] bytes)
    {
        var samples = new float[bytes.Length / 2];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BitConverter.ToInt16(bytes, index * 2) / 32768F;
        }
        return samples;
    }
}

static class Pcm16Wav
{
    public static float[] Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("The file is not a RIFF WAV file.");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("The file is not a WAV file.");

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var length = reader.ReadInt32();
            if (length < 0 || stream.Position + length > stream.Length) throw new InvalidDataException("The WAV file has an invalid chunk.");
            if (id == "fmt ")
            {
                if (length < 16) throw new InvalidDataException("The WAV format chunk is invalid.");
                var audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                if (audioFormat != 1) throw new InvalidDataException("Only PCM WAV audio is supported.");
                stream.Position += length - 16;
            }
            else if (id == "data")
            {
                data = reader.ReadBytes(length);
            }
            else
            {
                stream.Position += length;
            }
            if (length % 2 == 1) stream.Position++;
        }

        if (channels != 1 || sampleRate != 16000 || bitsPerSample != 16 || data is null) throw new InvalidDataException("Use 16 kHz, mono, PCM 16-bit WAV audio.");
        return Pcm16.ToFloats(data);
    }
}

static class SystemMetrics
{
    public static SystemMetricSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("Could not read Windows memory status.");
        var totalMb = status.TotalPhysical / 1024d / 1024d;
        var usedMb = (status.TotalPhysical - status.AvailablePhysical) / 1024d / 1024d;
        return new SystemMetricSnapshot(
            Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
            Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 1),
            Math.Round(process.PeakWorkingSet64 / 1024d / 1024d, 1),
            Math.Round(usedMb, 1),
            Math.Round(totalMb, 1),
            Math.Round(usedMb / Math.Max(totalMb, 1) * 100, 1));
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

sealed record SystemMetricSnapshot(
    [property: JsonPropertyName("service_working_set_mb")] double ServiceWorkingSetMb,
    [property: JsonPropertyName("service_private_memory_mb")] double ServicePrivateMemoryMb,
    [property: JsonPropertyName("service_peak_working_set_mb")] double ServicePeakWorkingSetMb,
    [property: JsonPropertyName("machine_memory_used_mb")] double MachineMemoryUsedMb,
    [property: JsonPropertyName("machine_total_memory_mb")] double MachineTotalMemoryMb,
    [property: JsonPropertyName("machine_memory_utilization_percent")] double MachineMemoryUtilizationPercent);
