using System.Text.Json;

namespace Ap.Control.Ui
{
    internal sealed record UiStatus(
        string Status,
        string Slot = "-",
        string Seed = "-",
        int? ChecksFound = null,
        int? ChecksTotal = null,
        int Pending = 0,
        string? Host = null,
        string? Port = null,
        IReadOnlyDictionary<int, bool>? Elevator = null)
    {
        internal static UiStatus Idle { get; } = new("Not connected");

        internal static UiStatus Connecting(string host) => new($"Connecting to {host}...");

        internal static UiStatus Failed(string reason) => new($"Failed: {reason}");

        internal string ToJs()
        {
            var payload = new Dictionary<string, object?>
            {
                ["status"] = Status,
                ["slot"] = Slot,
                ["seed"] = Seed,
            };
            if (ChecksFound is { } found && ChecksTotal is { } total)
                payload["checks"] = new Dictionary<string, object?> { ["found"] = found, ["total"] = total };
            else
                payload["checks"] = null;

            payload["pending"] = Pending > 0 ? Pending : null;

            payload["session"] = Host is null ? null : new Dictionary<string, object?>
            {
                ["host"] = Host,
                ["port"] = Port,
                ["slot"] = Slot,
            };

            string js = "if (window.AP && AP.update) AP.update(" + JsonSerializer.Serialize(payload) + ");";

            if (Elevator is not null)
            {
                var byId = Elevator.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
                js += "if (window.AP && AP.elevator) AP.elevator(" + JsonSerializer.Serialize(byId) + ");";
            }
            return js;
        }
    }
}
