// Quick probe: can the .NET managed SDK resolve the Nemotron alias
// now that Core has been bumped to the April 18 build?
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

await FoundryLocalManager.CreateAsync(
    new Configuration { AppName = "Hush" },
    NullLogger.Instance);

var manager = FoundryLocalManager.Instance;
var catalog = await manager.GetCatalogAsync(default);

foreach (var alias in new[] {
    "nemotron-speech-streaming-en-0.6b",
    "nemotron-speech-streaming-en-0.6b-generic-cpu",
})
{
    try
    {
        var m = await catalog.GetModelAsync(alias, default);
        Console.WriteLine(m is null
            ? $"  {alias}: NULL"
            : $"  {alias}: FOUND id={m.Id}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {alias}: ERROR {ex.GetType().Name}: {ex.Message}");
    }
}
