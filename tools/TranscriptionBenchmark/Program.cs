// Standalone Foundry Local transcription benchmark.
// Records from the default microphone in chunks and transcribes each one.
// Usage:  dotnet run [--seconds 30] [--model nemotron-speech-streaming-en-0.6b] [--language en] [--chunk-seconds 5]

using System.Diagnostics;
using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

// ── CLI args ────────────────────────────────────────────────────────────────
int durationSeconds = ArgInt(args, "--seconds", 30);
string modelAlias = ArgString(args, "--model", "nemotron-speech-streaming-en-0.6b");
string language = ArgString(args, "--language", "en");
int chunkSeconds = ArgInt(args, "--chunk-seconds", 5);

Console.WriteLine($"""
╔═══════════════════════════════════════════════════════╗
║  Foundry Local – Transcription Benchmark              ║
╠═══════════════════════════════════════════════════════╣
║  Model        : {modelAlias,-37}║
║  Language     : {language,-37}║
║  Duration     : {durationSeconds,3}s                                ║
║  Chunk size   : {chunkSeconds,3}s                                ║
╚═══════════════════════════════════════════════════════╝
""");

var totalSw = Stopwatch.StartNew();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── 1. Initialize Foundry Local runtime ─────────────────────────────────────
Console.Write("Initializing Foundry Local runtime... ");
var initSw = Stopwatch.StartNew();
await FoundryLocalManager.CreateAsync(
    new Configuration
    {
        AppName = "TranscriptionBenchmark",
        // TEMPORARY: matches Hush.Core's FoundryRuntimeConfiguration so pre-release / test
        // models (qwen3.5-*, qwen3-vl-*) are visible. Drop once they ship in the default catalog.
        AdditionalSettings = new Dictionary<string, string>
        {
            { "AzureCatalogFilter", "'',test" },
        },
    },
    NullLogger.Instance);
var manager = FoundryLocalManager.Instance;
initSw.Stop();
Console.WriteLine($"done ({initSw.ElapsedMilliseconds} ms)");

// ── 2. Download / cache model ───────────────────────────────────────────────
Console.Write($"Resolving model '{modelAlias}'... ");
var downloadSw = Stopwatch.StartNew();
var catalog = await manager.GetCatalogAsync(cts.Token);
var model = await catalog.GetModelAsync(modelAlias, cts.Token)
    ?? throw new InvalidOperationException($"Model '{modelAlias}' not found in catalog.");
await model.DownloadAsync((float pct) =>
{
    Console.Write($"\rDownloading model '{modelAlias}'... {pct:F0}%   ");
});
downloadSw.Stop();
Console.WriteLine($"\rModel ready ({downloadSw.ElapsedMilliseconds} ms)                    ");

// ── 3. Load model into runtime ──────────────────────────────────────────────
Console.Write("Loading model into runtime... ");
var loadSw = Stopwatch.StartNew();
await model.LoadAsync();
var audioClient = await model.GetAudioClientAsync();
audioClient.Settings.Language = language;
loadSw.Stop();
Console.WriteLine($"done ({loadSw.ElapsedMilliseconds} ms)");

// ── 4. Record and transcribe in chunks ──────────────────────────────────────
var waveFormat = new WaveFormat(rate: 16000, bits: 16, channels: 1);
int numChunks = (int)Math.Ceiling((double)durationSeconds / chunkSeconds);

Console.WriteLine();
Console.WriteLine($"🎙  Recording for {durationSeconds}s in {chunkSeconds}s chunks — speak now (Ctrl+C to stop early)");
Console.WriteLine(new string('─', 60));

bool firstChunk = true;
long firstTokenMs = 0;
var firstChunkSw = Stopwatch.StartNew();

long totalTranscriptionMs = 0;
int completedChunks = 0;
double totalAudioSeconds = 0;

for (int i = 0; i < numChunks && !cts.IsCancellationRequested; i++)
{
    int thisChunkSeconds = (i == numChunks - 1)
        ? durationSeconds - (i * chunkSeconds)
        : chunkSeconds;
    if (thisChunkSeconds <= 0) break;

    Console.Write($"  [chunk {i + 1}/{numChunks}] recording {thisChunkSeconds}s...");

    // Record audio into a buffer
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
        try { await Task.Delay(TimeSpan.FromSeconds(thisChunkSeconds), cts.Token); }
        catch (OperationCanceledException) { }
        waveIn.StopRecording();
    }

    if (audioBytes.Count == 0) break;

    // Write chunk to a temp WAV file
    var tempFile = Path.Combine(Path.GetTempPath(), $"hush_bench_{i}.wav");
    using (var writer = new WaveFileWriter(tempFile, waveFormat))
        writer.Write(audioBytes.ToArray(), 0, audioBytes.Count);

    // Transcribe the chunk
    Console.Write(" transcribing...");
    var transcribeSw = Stopwatch.StartNew();
    string text;
    try
    {
        var result = await audioClient.TranscribeAudioAsync(tempFile, cts.Token);
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
    Console.WriteLine($"    → {(string.IsNullOrEmpty(text) ? "(silence)" : text)}");
}

totalSw.Stop();

// ── 5. Print summary ────────────────────────────────────────────────────────
long avgTranscriptionMs = completedChunks > 0 ? totalTranscriptionMs / completedChunks : 0;
Console.WriteLine();
Console.WriteLine(new string('═', 60));
Console.WriteLine("  BENCHMARK RESULTS");
Console.WriteLine(new string('─', 60));
Console.WriteLine($"  Runtime init        : {initSw.ElapsedMilliseconds,7} ms");
Console.WriteLine($"  Model download      : {downloadSw.ElapsedMilliseconds,7} ms");
Console.WriteLine($"  Model load          : {loadSw.ElapsedMilliseconds,7} ms");
Console.WriteLine($"  Time to first token : {firstTokenMs,7} ms");
Console.WriteLine($"  Avg transcription   : {avgTranscriptionMs,7} ms / chunk");
Console.WriteLine($"  Total wall time     : {totalSw.ElapsedMilliseconds,7} ms");
Console.WriteLine($"  Audio transcribed   : {totalAudioSeconds,7:F1} s  ({completedChunks} chunks)");
Console.WriteLine(new string('═', 60));

// ── Helpers ─────────────────────────────────────────────────────────────────
static int ArgInt(string[] args, string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

static string ArgString(string[] args, string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
