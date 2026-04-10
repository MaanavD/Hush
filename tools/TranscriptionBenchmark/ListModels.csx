using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

await FoundryLocalManager.CreateAsync(new Configuration { AppName = "CatalogList" }, NullLogger.Instance);
var manager = FoundryLocalManager.Instance;
var catalog = await manager.GetCatalogAsync();
var models = await catalog.GetModelsAsync();
foreach (var m in models)
    Console.WriteLine($"{m.Alias,-40} {m.Task,-20} {m.ModelId}");
