// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Audio;
using Hush.Core.Transcription;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ── Hush — Milestone 1: Proof of Life ──────────────────────────────────────
// Run this console app to verify mic → Foundry Local → text on Windows.
//   dotnet run -- --list       list available catalog models and exit
//   dotnet run -- --model <alias>   use a specific model alias (default: nemotron-speech-streaming-en-0.6b)
//   Press Ctrl+C to stop recording.
// ───────────────────────────────────────────────────────────────────────────

bool listOnly = args.Contains("--list");
string modelAlias = Hush.Core.Configuration.HushSettings.DefaultTranscriptionModel;
int modelIdx = Array.IndexOf(args, "--model");
if (modelIdx >= 0 && modelIdx + 1 < args.Length)
    modelAlias = args[modelIdx + 1];

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));

var logger = loggerFactory.CreateLogger<TranscriptionEngine>();

Console.WriteLine("Hush — Proof of Life");
Console.WriteLine("Initializing Foundry Local...");

await FoundryLocalManager.CreateAsync(
    FoundryRuntimeConfiguration.Create("Hush", logger),
    NullLogger.Instance);

var manager = FoundryLocalManager.Instance;
var catalog = await manager.GetCatalogAsync();

if (listOnly)
{
    Console.WriteLine();
    Console.WriteLine("Available models in Foundry Local catalog:");
    Console.WriteLine("─────────────────────────────────────────────");

    var models = await catalog.ListModelsAsync();
    if (!models.Any())
    {
        Console.WriteLine("  (none — ensure Foundry Local is installed and running)");
    }
    else
    {
        foreach (var m in models)
        {
            var cached = await m.IsCachedAsync() ? "cached" : "not cached";
            Console.WriteLine($"  {m.Alias,-30} [{m.Id}]  {cached}");
        }
    }
    return;
}

Console.WriteLine($"Loading model '{modelAlias}'...");
Console.WriteLine("(This may download the model on first run — could take a few minutes.)");
Console.WriteLine();

var progress = new Progress<double>(p =>
    Console.Write($"\r  Download: {p:P0}   "));

var engine = new TranscriptionEngine(logger);
await engine.InitializeAsync(
    modelAlias: modelAlias,
    downloadProgress: progress);

Console.WriteLine("\rModel ready.                              ");
Console.WriteLine();
Console.WriteLine("Starting session. Speak now — press Ctrl+C to stop.");
Console.WriteLine("─────────────────────────────────────────────────────");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var capture = new AudioCaptureService(
    loggerFactory.CreateLogger<AudioCaptureService>());

await engine.StartSessionAsync(cancellationToken: cts.Token);

// Feed microphone audio into the transcription engine.
capture.Start(engine.AppendAudioAsync);

// Print results as they arrive from the engine's batch loop.
try
{
    await foreach (var result in engine.GetResultStreamAsync(cts.Token))
    {
        var tag = result.IsFinal ? "[final]" : "[interim]";
        Console.WriteLine($"{tag} {result.DisplayText}");
    }
}
catch (OperationCanceledException) { }

capture.Stop();
await engine.StopSessionAsync();

Console.WriteLine();
Console.WriteLine("Session ended.");
