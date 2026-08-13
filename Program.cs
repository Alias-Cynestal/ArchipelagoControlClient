using Ap.Control;
using Ap.Control.Models;
using Ap.Control.Ui;
using Ap.Control.Utils;
using Ap.Control.Utils.Interfaces;
using Ap.Control.Memory;
using Ap.Control.SaveFile;

return await RunClientAsync(args);

static async Task<int> RunClientAsync(string[] args)
{
    string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

    string? server = Arg("--server");
    string? username = Arg("--username");
    string? password = Arg("--password");
    string? source = Arg("--source");
    string? savePath = Arg("--save");
    string? itemsPath = Arg("--items");
    string? portText = Arg("--ui-port");

    if (args.Contains("--help") || args.Contains("-h"))
    {
        Console.WriteLine(
            "Usage: Ap.Control [--server <url> --username <name> [--password <pass>]]\n" +
            "                  [--source memory|file] [--save <path>] [--items <apitems.json>]\n" +
            "                  [--ui-port <n>] [--no-ui]\n\n" +
            "With --server and --username the client connects on startup as before. Without them it\n" +
            "waits for the in-game Archipelago page to supply the details.");
        return 0;
    }

    if (!int.TryParse(portText ?? UiBridge.DefaultPort.ToString(), out int uiPort))
    {
        Console.Error.WriteLine($"[ui] --ui-port must be a number, got '{portText}'");
        return 1;
    }

    await using var granter = new NativeItemGranter();
    await using var abilityGranter = new NativeAbilityGranter();
    using var gameflow = new NativeGameFlowController();

    ApItemMap itemMap;
    try
    {
        string mapPath = itemsPath ?? Path.Combine(AppContext.BaseDirectory, "apitems.json");
        string mapSource;
        if (File.Exists(mapPath))
        {
            itemMap = ApItemMap.Load(mapPath);
            mapSource = mapPath;
        }
        else if (EmbeddedItemMap() is { } embedded)
        {
            itemMap = ApItemMap.Parse(embedded);
            mapSource = "built-in copy";
        }
        else
        {
            itemMap = ApItemMap.Empty;
            mapSource = "none";
        }

        Console.WriteLine(itemMap.IsEmpty
            ? "Item map: none — clearance items still resolve (built in); everything else falls back to inventory GID (pass --items <path> to route sectors/keys)."
            : $"Item map: {itemMap.Count} entries loaded from {mapSource} (clearance items resolve built-in).");
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"[item-map] failed to load: {e.Message}");
        return 1;
    }
    Console.WriteLine(GameBuildRegistry.StartupBanner());

    var relay = new SaveNotifierRelay();
    using var session = new ApSessionHost(granter, abilityGranter, gameflow, itemMap, relay);

    UiBridge? bridge = null;
    if (!args.Contains("--no-ui"))
    {
        try
        {
            var candidate = new UiBridge(uiPort);
            candidate.ConnectRequested += session.ConnectAsync;
            candidate.StatusRequested += session.RefreshAsync;
            candidate.Start();

            session.StatusChanged += candidate.PushAsync;
            bridge = candidate;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[ui] bridge unavailable: {e.Message}");
            bridge = null;
        }
    }

    try
    {
        if (server is not null && username is not null)
        {
            await session.ConnectAsync(new ConnectRequest(server, "", username, password)).ConfigureAwait(false);
            if (session.Status.Status.StartsWith("failed", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[failed] {session.Status.Status}");
                if (bridge is null) return 1;
            }
        }
        else if (bridge is null)
        {
            Console.Error.WriteLine(
                "Nothing to do: no --server/--username given and the UI bridge is not running.");
            return 1;
        }
        else
        {
            Console.WriteLine("Waiting for the in-game Archipelago page to supply connection details...");
        }

        bool useFile = string.Equals(source, "file", StringComparison.OrdinalIgnoreCase);
        ISaveWatcher watcher = useFile
            ? new SaveFileWatcher(savePath ?? Path.Combine(AppContext.BaseDirectory, "samples", "persistent.chunk"))
            : new SaveMemoryWatcher();
        Console.WriteLine(useFile ? "Save source: file" : "Save source: process memory (Control_DX12)");

        using (watcher)
        {
            watcher.Error += (_, e) => Console.Error.WriteLine($"[save-watch] {e.Exception.Message}");
            watcher.AddNotifier(relay);
            await watcher.StartAsync(emitInitial: true);

            using var quit = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
            try { await Task.Delay(Timeout.Infinite, quit.Token); }
            catch (OperationCanceledException) { /* Ctrl+C */ }
        }
        return 0;
    }
    finally
    {
        if (bridge is not null) await bridge.DisposeAsync();
    }
}

/// <summary>
/// The apitems.json embedded at build time, or null if this build has none. Named explicitly via
/// LogicalName in the .csproj so the lookup does not depend on the root namespace.
/// </summary>
static string? EmbeddedItemMap()
{
    using Stream? s = System.Reflection.Assembly.GetExecutingAssembly()
        .GetManifestResourceStream("apitems.json");
    if (s is null) return null;
    using var reader = new StreamReader(s);
    return reader.ReadToEnd();
}
