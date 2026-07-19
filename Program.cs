using Ap.Control;
using Ap.Control.Models;
using Ap.Control.Utils;
using Ap.Control.Utils.Interfaces;
using Ap.Control.Memory;
using Ap.Control.SaveFile;

return await RunClientAsync(args);

static async Task<int> RunClientAsync(string[] args)
{
    string? server = args.SkipWhile(a => a != "--server").Skip(1).FirstOrDefault();
    string? username = args.SkipWhile(a => a != "--username").Skip(1).FirstOrDefault();
    string? password = args.SkipWhile(a => a != "--password").Skip(1).FirstOrDefault();
    string? source = args.SkipWhile(a => a != "--source").Skip(1).FirstOrDefault();
    string? savePath = args.SkipWhile(a => a != "--save").Skip(1).FirstOrDefault();
    if (server is null || username is null)
    {
        Console.Error.WriteLine(
            "Usage: Ap.Control --server <url> --username <name> [--password <pass>] " +
            "[--source memory|file] [--save <path>] [--items <apitems.json>]");
        return 1;
    }
    string? itemsPath = args.SkipWhile(a => a != "--items").Skip(1).FirstOrDefault();
    var model = new ArchipelagoConnectionModel(new Uri(server), username, password);
    await using var granter = new NativeItemGranter();
    using var gameflow = new NativeGameFlowController();
    using var uiModel = new NativeUiModelController();

    ApItemMap itemMap;
    try
    {
        string mapPath = itemsPath ?? Path.Combine(AppContext.BaseDirectory, "apitems.json");
        itemMap = File.Exists(mapPath) ? ApItemMap.Load(mapPath) : ApItemMap.Empty;
        Console.WriteLine(itemMap.IsEmpty
            ? "Item map: none — clearance items still resolve (built in); everything else falls back to inventory GID (pass --items <path> to route sectors/keys)."
            : $"Item map: {itemMap.Count} entries loaded from {mapPath} (clearance items resolve built-in).");
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"[item-map] failed to load: {e.Message}");
        return 1;
    }

    using var client = new ArchipelagoClient(model, granter, gameflow, uiModel, itemMap);
    try
    {
        await client.StartClient();
        Console.WriteLine("Connected to Archipelago server.");
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"[failed] {e.Message}");
        return 1;
    }
    Console.WriteLine("Listening for new locations unlocked...");

    // Default to reading the save straight from the running game's memory (no file path needed);
    // pass "--source file [--save <path>]" to watch persistent.chunk on disk instead.
    bool useFile = string.Equals(source, "file", StringComparison.OrdinalIgnoreCase);
    ISaveWatcher watcher = useFile
        ? new SaveFileWatcher(savePath ?? Path.Combine(AppContext.BaseDirectory, "samples", "persistent.chunk"))
        : new SaveMemoryWatcher();
    Console.WriteLine(useFile ? "Save source: file" : "Save source: process memory (Control_DX12)");

    using (watcher)
    {
        watcher.Error += (_, e) => Console.Error.WriteLine($"[save-watch] {e.Exception.Message}");
        watcher.AddNotifier(client);
        await watcher.StartAsync(emitInitial: true);

        using var quit = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, quit.Token); }
        catch (OperationCanceledException) { /* Ctrl+C */ }
    }
    return 0;
}
