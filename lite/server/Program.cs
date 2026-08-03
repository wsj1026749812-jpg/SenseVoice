using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var config = ServiceConfig.FromArgs(args);
config.Validate();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(config.ListenUrl);
var app = builder.Build();
var inferenceGate = new SemaphoreSlim(1, 1);

app.MapGet("/", () => Results.Ok(new { service = "sensevoice-lite", health = "/health" }));
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    device = "cpu",
    runtime = "llama.cpp/GGUF",
    model = Path.GetFileName(config.ModelPath),
    vad_model = Path.GetFileName(config.VadModelPath),
}));

app.MapPost("/api/v1/asr", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { detail = "Use multipart/form-data with one or more files fields." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var language = form["lang"].FirstOrDefault() ?? "auto";
    if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
    {
        return Results.UnprocessableEntity(new
        {
            detail = "The GGUF runtime currently supports automatic language detection only. Use lang=auto."
        });
    }

    var uploads = form.Files
        .Where(file => string.Equals(file.Name, "files", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    if (uploads.Length == 0)
    {
        return Results.UnprocessableEntity(new { detail = "At least one files field is required." });
    }
    if (uploads.Any(file => !string.Equals(Path.GetExtension(file.FileName), ".wav", StringComparison.OrdinalIgnoreCase)))
    {
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }

    var requestDirectory = Path.Combine(Path.GetTempPath(), "sensevoice-lite", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(requestDirectory);
    try
    {
        var result = new List<TranscriptionResult>(uploads.Length);
        await inferenceGate.WaitAsync(cancellationToken);
        try
        {
            for (var index = 0; index < uploads.Length; index++)
            {
                var upload = uploads[index];
                var inputPath = Path.Combine(requestDirectory, $"audio-{index}.wav");
                await using (var target = File.Create(inputPath))
                {
                    await upload.CopyToAsync(target, cancellationToken);
                }

                result.Add(await NativeRunner.TranscribeAsync(config, inputPath, upload.FileName));
            }
        }
        finally
        {
            inferenceGate.Release();
        }

        return Results.Ok(new { result });
    }
    catch (NativeInferenceException exception)
    {
        return Results.UnprocessableEntity(new { detail = exception.Message });
    }
    finally
    {
        Directory.Delete(requestDirectory, recursive: true);
    }
});

app.Run();

sealed record ServiceConfig(string ListenUrl, string RunnerPath, string ModelPath, string VadModelPath)
{
    public static ServiceConfig FromArgs(string[] args)
    {
        var root = AppContext.BaseDirectory;
        var listenUrl = "http://127.0.0.1:50000";
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--url" && index + 1 < args.Length)
            {
                listenUrl = args[++index];
            }
        }

        return new ServiceConfig(
            listenUrl,
            Path.Combine(root, "runtime", "llama-funasr-sensevoice.exe"),
            Path.Combine(root, "models", "sensevoice-small-q8.gguf"),
            Path.Combine(root, "models", "fsmn-vad.gguf"));
    }

    public void Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("--url must be an absolute http:// URL.");
        }
        foreach (var path in new[] { RunnerPath, ModelPath, VadModelPath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Required SenseVoice Lite file was not found.", path);
            }
        }
    }
}

static class NativeRunner
{
    private static readonly Regex TagPattern = new("<\\|([^|]+)\\|>", RegexOptions.Compiled);

    public static async Task<TranscriptionResult> TranscribeAsync(ServiceConfig config, string inputPath, string filename)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.RunnerPath,
            WorkingDirectory = Path.GetDirectoryName(config.RunnerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(config.ModelPath);
        startInfo.ArgumentList.Add("--vad");
        startInfo.ArgumentList.Add(config.VadModelPath);
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--keep-tags");

        using var process = Process.Start(startInfo) ?? throw new NativeInferenceException("Could not start the SenseVoice GGUF runtime.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new NativeInferenceException($"SenseVoice inference failed: {detail.Trim()}");
        }

        var rawText = output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !line.StartsWith("[sensevoice]", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new NativeInferenceException("SenseVoice returned no transcription text.");
        }

        var tags = TagPattern.Matches(rawText).Select(match => match.Groups[1].Value).ToArray();
        var cleanText = TagPattern.Replace(rawText, string.Empty).Trim();
        return new TranscriptionResult
        {
            Filename = filename,
            RawText = rawText,
            CleanText = cleanText,
            Text = cleanText,
            Language = tags.ElementAtOrDefault(0),
            Emotion = tags.ElementAtOrDefault(1),
            Event = tags.ElementAtOrDefault(2),
            Itn = tags.ElementAtOrDefault(3),
        };
    }
}

sealed class NativeInferenceException(string message) : Exception(message);

sealed class TranscriptionResult
{
    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("raw_text")]
    public required string RawText { get; init; }

    [JsonPropertyName("clean_text")]
    public required string CleanText { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("emotion")]
    public string? Emotion { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("itn")]
    public string? Itn { get; init; }
}
