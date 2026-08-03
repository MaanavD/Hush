// Probe: verify the post-processing model initializes and actually rewrites text.
using Hush.Core.Configuration;
using Hush.Core.PostProcessing;
using Hush.Core.Transcription;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

var modelAlias = args.Length > 0 ? args[0] : "qwen3-0.6b";

await FoundryLocalManager.CreateAsync(
    FoundryRuntimeConfiguration.Create("Hush"),
    NullLogger.Instance);

var manager = FoundryLocalManager.Instance;
var catalog = await manager.GetCatalogAsync(default);
var models = (await catalog.ListModelsAsync()).ToList();
Console.WriteLine($"Total models: {models.Count}");
Console.WriteLine($"{modelAlias} in list: {models.Any(m => m.Alias == modelAlias)}");

var target = await catalog.GetModelAsync(modelAlias, default);
if (target is null)
{
    Console.WriteLine($"GetModelAsync('{modelAlias}') -> NULL");
    return;
}

Console.WriteLine($"FOUND id={target.Id} cached={await target.IsCachedAsync()}");

await using var postProcessor = new FoundryPostProcessingService();
var statusProgress = new Progress<string>(s => Console.WriteLine($"status: {s}"));
var downloadProgress = new Progress<double>(p => Console.WriteLine($"download: {p:P0}"));

await postProcessor.InitializeAsync(
    modelAlias,
    downloadProgress,
    statusProgress);

Console.WriteLine($"IsReady: {postProcessor.IsReady}");
Console.WriteLine();

const string sentinelRaw = "this text should be ignored by the sentinel probe";
const string sentinelPrompt =
    "This is a diagnostic prompt. Output exactly this token and nothing else: CLEANING_PROBE_WORKED\n\nInput:\n{input}";

var sentinel = await postProcessor.RewriteAsync(sentinelRaw, sentinelPrompt);
Console.WriteLine("Sentinel raw:");
Console.WriteLine(sentinelRaw);
Console.WriteLine("Sentinel output:");
Console.WriteLine(sentinel ?? "<null>");
Console.WriteLine();

const string cleanRaw =
    "um I I think we should move the design review to Thursday morning. actually wait Thursday afternoon is better because the partner meeting is already on the calendar you know";
var cleanPrompt = HushSettings.BuiltInPrompts.First(p => p.Id == "clean-dictation").Prompt;
var cleaned = await postProcessor.RewriteAsync(cleanRaw, cleanPrompt);

Console.WriteLine("Clean raw:");
Console.WriteLine(cleanRaw);
Console.WriteLine("Clean output:");
Console.WriteLine(cleaned ?? "<null>");
