// Standalone Foundry Local transcription benchmark.
// Usage:
//   dotnet run -- --audio-file sample.wav --model whisper-tiny --json
//   dotnet run -- --seconds 30 --model nemotron-speech-streaming-en-0.6b --language en --chunk-seconds 5

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

int durationSeconds = ArgInt(args, "--seconds", 30);
string modelAlias = ArgString(args, "--model", "nemotron-speech-streaming-en-0.6b");
string language = ArgString(args, "--language", "en");
int chunkSeconds = ArgInt(args, "--chunk-seconds", 5);
string? audioFile = ArgOptionalString(args, "--audio-file");
bool json = ArgBool(args, "--json");

var totalSw = Stopwatch.StartNew();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

void WriteStatus(string message)
{
    if (!json)
        Console.Write(message);
}

void WriteStatusLine(string message = "")
{
    if (!json)
        Console.WriteLine(message);
}

WriteStatusLine("Foundry Local transcription benchmark");
WriteStatusLine($"Model    : {modelAlias}");
WriteStatusLine($"Language : {language}");
if (audioFile is not null)
    WriteStatusLine($"Audio    : {audioFile}");
else
    WriteStatusLine($"Duration : {durationSeconds}s; chunk size: {chunkSeconds}s");
WriteStatusLine();

WriteStatus("Initializing Foundry Local runtime... ");
var initSw = Stopwatch.StartNew();
await FoundryLocalManager.CreateAsync(
    new Configuration
    {
        AppName = "TranscriptionBenchmark",
        AdditionalSettings = new Dictionary<string, string>
        {
            { "AzureCatalogFilter", "'',test" },
        },
    },
    NullLogger.Instance);
var manager = FoundryLocalManager.Instance;
initSw.Stop();
WriteStatusLine($"done ({initSw.ElapsedMilliseconds} ms)");

WriteStatus($"Resolving model '{modelAlias}'... ");
var lookupSw = Stopwatch.StartNew();
var catalog = await manager.GetCatalogAsync(cts.Token);
var model = await catalog.GetModelAsync(modelAlias, cts.Token)
    ?? throw new InvalidOperationException($"Model '{modelAlias}' not found in catalog.");
lookupSw.Stop();
WriteStatusLine($"done ({lookupSw.ElapsedMilliseconds} ms)");

WriteStatus($"Checking model '{modelAlias}' cache... ");
var downloadSw = Stopwatch.StartNew();
await model.DownloadAsync((float pct) =>
{
    if (!json)
        Console.Write($"\rDownloading model '{modelAlias}'... {pct:F0}%   ");
});
downloadSw.Stop();
WriteStatusLine($"\rModel ready ({downloadSw.ElapsedMilliseconds} ms)                    ");

WriteStatus("Loading model into runtime... ");
var loadSw = Stopwatch.StartNew();
await model.LoadAsync();
var audioClient = await model.GetAudioClientAsync();
audioClient.Settings.Language = language;
loadSw.Stop();
WriteStatusLine($"done ({loadSw.ElapsedMilliseconds} ms)");

if (audioFile is not null)
{
    var result = await RunFileBenchmarkAsync(
        audioClient,
        audioFile,
        modelAlias,
        language,
        initSw.ElapsedMilliseconds,
        lookupSw.ElapsedMilliseconds,
        downloadSw.ElapsedMilliseconds,
        loadSw.ElapsedMilliseconds,
        cts.Token);

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, BenchmarkJsonContext.Default.FileBenchmarkResult));
    }
    else
    {
        WriteStatusLine();
        WriteStatusLine("Benchmark result");
        WriteStatusLine($"Transcription : {result.transcription_ms} ms");
        WriteStatusLine($"Transcript    : {result.transcript}");
    }

    return;
}

await RunMicrophoneBenchmarkAsync(
    audioClient,
    durationSeconds,
    chunkSeconds,
    initSw.ElapsedMilliseconds,
    lookupSw.ElapsedMilliseconds,
    downloadSw.ElapsedMilliseconds,
    loadSw.ElapsedMilliseconds,
    totalSw,
    cts.Token);

static async Task<FileBenchmarkResult> RunFileBenchmarkAsync(
    OpenAIAudioClient audioClient,
    string audioFile,
    string modelAlias,
    string language,
    long runtimeInitMs,
    long modelLookupMs,
    long modelDownloadMs,
    long modelLoadMs,
    CancellationToken cancellationToken)
{
    if (!File.Exists(audioFile))
        throw new FileNotFoundException("Audio file does not exist.", audioFile);

    var transcribeSw = Stopwatch.StartNew();
    var result = await audioClient.TranscribeAudioAsync(audioFile, cancellationToken);
    transcribeSw.Stop();
    if (!result.Successful)
        throw new InvalidOperationException(result.Error?.Message ?? "Audio transcription failed.");

    string transcript = result.Text?.Trim() ?? string.Empty;
    return new FileBenchmarkResult(
        implementation: "dotnet-foundry-local",
        model_alias: modelAlias,
        audio_file: audioFile,
        language: language,
        runtime_init_ms: runtimeInitMs,
        model_lookup_ms: modelLookupMs,
        model_download_ms: modelDownloadMs,
        model_load_ms: modelLoadMs,
        transcription_ms: transcribeSw.ElapsedMilliseconds,
        streaming_transcription_ms: null,
        transcript_chars: transcript.Length,
        transcript: transcript,
        streaming_transcript: null);
}

static async Task RunMicrophoneBenchmarkAsync(
    OpenAIAudioClient audioClient,
    int durationSeconds,
    int chunkSeconds,
    long runtimeInitMs,
    long modelLookupMs,
    long modelDownloadMs,
    long modelLoadMs,
    Stopwatch totalSw,
    CancellationToken cancellationToken)
{
    var waveFormat = new WaveFormat(rate: 16000, bits: 16, channels: 1);
    int numChunks = (int)Math.Ceiling((double)durationSeconds / chunkSeconds);

    Console.WriteLine();
    Console.WriteLine($"Recording for {durationSeconds}s in {chunkSeconds}s chunks - speak now (Ctrl+C to stop early)");
    Console.WriteLine(new string('-', 60));

    bool firstChunk = true;
    long firstTokenMs = 0;
    var firstChunkSw = Stopwatch.StartNew();

    long totalTranscriptionMs = 0;
    int completedChunks = 0;
    double totalAudioSeconds = 0;

    for (int i = 0; i < numChunks && !cancellationToken.IsCancellationRequested; i++)
    {
        int thisChunkSeconds = (i == numChunks - 1)
            ? durationSeconds - (i * chunkSeconds)
            : chunkSeconds;
        if (thisChunkSeconds <= 0) break;

        Console.Write($"  [chunk {i + 1}/{numChunks}] recording {thisChunkSeconds}s...");

        var audioBytes = new List<byte>(thisChunkSeconds * 16000 * 2);
        using (var waveIn = new WaveInEvent { WaveFormat = waveFormat, BufferMilliseconds = 50 })
        {
            waveIn.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    var chunk = new byte[e.BytesRecorded];
                    Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
                    audioBytes.AddRange(chunk);
                }
            };
            waveIn.StartRecording();
            try { await Task.Delay(TimeSpan.FromSeconds(thisChunkSeconds), cancellationToken); }
            catch (OperationCanceledException) { }
            waveIn.StopRecording();
        }

        if (audioBytes.Count == 0) break;

        var tempFile = Path.Combine(Path.GetTempPath(), $"hush_bench_{i}.wav");
        using (var writer = new WaveFileWriter(tempFile, waveFormat))
            writer.Write(audioBytes.ToArray(), 0, audioBytes.Count);

        Console.Write(" transcribing...");
        var transcribeSw = Stopwatch.StartNew();
        string text;
        try
        {
            var result = await audioClient.TranscribeAudioAsync(tempFile, cancellationToken);
            if (!result.Successful)
            {
                Console.WriteLine($" ERROR: {result.Error?.Message}");
                continue;
            }
            text = result.Text?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine($" ERROR: {ex.Message}");
            continue;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
        transcribeSw.Stop();

        if (firstChunk) { firstTokenMs = firstChunkSw.ElapsedMilliseconds; firstChunk = false; }

        totalTranscriptionMs += transcribeSw.ElapsedMilliseconds;
        completedChunks++;
        totalAudioSeconds += thisChunkSeconds;

        Console.WriteLine($" ({transcribeSw.ElapsedMilliseconds} ms)");
        Console.WriteLine($"    -> {(string.IsNullOrEmpty(text) ? "(silence)" : text)}");
    }

    totalSw.Stop();
    long avgTranscriptionMs = completedChunks > 0 ? totalTranscriptionMs / completedChunks : 0;
    Console.WriteLine();
    Console.WriteLine(new string('=', 60));
    Console.WriteLine("  BENCHMARK RESULTS");
    Console.WriteLine(new string('-', 60));
    Console.WriteLine($"  Runtime init        : {runtimeInitMs,7} ms");
    Console.WriteLine($"  Model lookup        : {modelLookupMs,7} ms");
    Console.WriteLine($"  Model download      : {modelDownloadMs,7} ms");
    Console.WriteLine($"  Model load          : {modelLoadMs,7} ms");
    Console.WriteLine($"  Time to first token : {firstTokenMs,7} ms");
    Console.WriteLine($"  Avg transcription   : {avgTranscriptionMs,7} ms / chunk");
    Console.WriteLine($"  Total wall time     : {totalSw.ElapsedMilliseconds,7} ms");
    Console.WriteLine($"  Audio transcribed   : {totalAudioSeconds,7:F1} s  ({completedChunks} chunks)");
    Console.WriteLine(new string('=', 60));
}

static bool ArgBool(string[] args, string name) => Array.IndexOf(args, name) >= 0;

static int ArgInt(string[] args, string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

static string ArgString(string[] args, string name, string fallback) =>
    ArgOptionalString(args, name) ?? fallback;

static string? ArgOptionalString(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

internal sealed record FileBenchmarkResult(
    string implementation,
    string model_alias,
    string audio_file,
    string language,
    long runtime_init_ms,
    long model_lookup_ms,
    long model_download_ms,
    long model_load_ms,
    long transcription_ms,
    long? streaming_transcription_ms,
    int transcript_chars,
    string transcript,
    string? streaming_transcript);

[JsonSerializable(typeof(FileBenchmarkResult))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
