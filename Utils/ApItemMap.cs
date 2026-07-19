using System.Text.Json;
using Ap.Control.Models;
using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Utils
{
    /// <summary>
    /// Maps Archipelago item ids to the in-game action they should trigger. The mapping is data owned
    /// by the AP world, loaded from a JSON file so the client stays generic. Format:
    /// <code>
    /// {
    ///   "items": {
    ///     "1001": { "kind": "Inventory",  "gid": "0x3AE684B975D8804D" },
    ///     "1002": { "kind": "Flag",       "flag": "ExecutiveElevator_CanTravel_Research" },
    ///     "1003": { "kind": "Clearance",  "level": 3 },
    ///     "1005": { "kind": "ProgressiveClearance" }
    ///   }
    /// }
    /// </code>
    /// </summary>
    public sealed class ApItemMap
    {
        private readonly Dictionary<long, ApItemAction> _byId;
        private readonly IReadOnlyCollection<string> _flagNames;

        public int Count => _byId.Count;
        public bool IsEmpty => _byId.Count == 0;

        /// <summary>
        /// Every distinct GameFlow flag name the map controls (all Flag-kind entries)
        /// </summary>
        public IReadOnlyCollection<string> FlagNames => _flagNames;

        private ApItemMap(Dictionary<long, ApItemAction> byId)
        {
            _byId = byId;
            _flagNames = byId.Values
                .Where(a => a.Kind == ApActionKind.Flag && a.Flags is { Count: > 0 })
                .SelectMany(a => a.Flags!)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct()
                .ToArray();
        }

        public static ApItemMap Empty { get; } = new(new Dictionary<long, ApItemAction>());

        /// <summary>
        /// Resolve an Archipelago item id to its action.
        /// </summary>
        public bool TryResolve(long itemId, out ApItemAction action)
        {
            if (_byId.TryGetValue(itemId, out action!))
                return true;

            if (ApClearanceIds.TryGetLevel(itemId, out int level))
            {
                action = ApItemAction.ForClearance(level);
                return true;
            }
            if (ApClearanceIds.IsProgressive(itemId))
            {
                action = ApItemAction.ProgressiveClearance;
                return true;
            }
            return false;
        }

        /// <summary>Load a map from JSON. Throws on malformed files so a bad table fails fast at startup.</summary>
        public static ApItemMap Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var byId = new Dictionary<long, ApItemAction>();

            if (!doc.RootElement.TryGetProperty("items", out var items))
                throw new FormatException("item map: missing top-level \"items\" object.");

            foreach (var entry in items.EnumerateObject())
            {
                if (!TryParseItemId(entry.Name, out long id))
                    throw new FormatException(
                        $"item map: item key \"{entry.Name}\" is not an integer id (decimal, or 0x-prefixed hex).");

                var e = entry.Value;
                string kindStr = e.GetProperty("kind").GetString()
                    ?? throw new FormatException($"item map: item {id} has no \"kind\".");
                if (!Enum.TryParse<ApActionKind>(kindStr, ignoreCase: true, out var kind))
                    throw new FormatException($"item map: item {id} has unknown kind \"{kindStr}\".");

                byId[id] = kind switch
                {
                    ApActionKind.Inventory => ApItemAction.ForInventory(ParseGid(e, id)),
                    ApActionKind.Flag => ApItemAction.ForFlags(ParseFlags(e, id), ParseBits(e, id)),
                    ApActionKind.Clearance => ApItemAction.ForClearance(e.GetProperty("level").GetInt32()),
                    ApActionKind.ProgressiveClearance => ApItemAction.ProgressiveClearance,
                    _ => throw new FormatException($"item map: item {id} unsupported kind."),
                };
            }
            return new ApItemMap(byId);
        }

        /// <summary>
        /// Parse an item-map key into the same 64-bit shape as <c>ItemInfo.ItemId</c>.
        /// </summary>
        private static bool TryParseItemId(string s, out long id)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ulong.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ulong hex))
                {
                    id = unchecked((long)hex);
                    return true;
                }
                id = 0;
                return false;
            }
            if (long.TryParse(s, out id)) return true;
            if (ulong.TryParse(s, out ulong u)) { id = unchecked((long)u); return true; }
            id = 0;
            return false;
        }

        /// <summary>
        /// Read a Flag entry's target flags.
        /// </summary>
        private static IReadOnlyList<string> ParseFlags(JsonElement e, long id)
        {
            if (e.TryGetProperty("flags", out var arr))
            {
                if (arr.ValueKind != JsonValueKind.Array)
                    throw new FormatException($"item map: item {id} (Flag) has a \"flags\" that is not an array.");
                var list = arr.EnumerateArray()
                    .Select(x => x.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .ToArray();
                if (list.Length == 0)
                    throw new FormatException($"item map: item {id} (Flag) has an empty \"flags\" array.");
                return list;
            }
            if (e.TryGetProperty("flag", out var one) && one.GetString() is { Length: > 0 } name)
                return new[] { name };
            throw new FormatException($"item map: item {id} (Flag) has neither \"flag\" nor \"flags\".");
        }

        /// <summary>
        /// Optional <c>"bits": ["Research", ...]</c> — which elevator UI bits this item grants
        /// </summary>
        private static IReadOnlyList<ElevatorBit>? ParseBits(JsonElement e, long id)
        {
            if (!e.TryGetProperty("bits", out var arr)) return null;
            if (arr.ValueKind != JsonValueKind.Array)
                throw new FormatException($"item map: item {id} has a \"bits\" that is not an array.");

            var list = new List<ElevatorBit>();
            foreach (var x in arr.EnumerateArray())
            {
                string s = x.GetString()
                    ?? throw new FormatException($"item map: item {id} has a non-string entry in \"bits\".");
                if (!Enum.TryParse<ElevatorBit>(s, ignoreCase: true, out var bit))
                    throw new FormatException(
                        $"item map: item {id} has unknown bit \"{s}\" (expected one of: "
                        + string.Join(", ", Enum.GetNames<ElevatorBit>()) + ").");
                list.Add(bit);
            }
            if (list.Count == 0)
                throw new FormatException($"item map: item {id} has an empty \"bits\" array.");
            return list;
        }

        private static ulong ParseGid(JsonElement e, long id)
        {
            if (!e.TryGetProperty("gid", out var g))
                throw new FormatException($"item map: item {id} (Inventory) has no \"gid\".");
            // Accept "0x..." hex strings or a JSON number.
            if (g.ValueKind == JsonValueKind.String)
            {
                string s = g.GetString()!.Trim();
                return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt64(s[2..], 16)
                    : ulong.Parse(s);
            }
            return g.GetUInt64();
        }
    }
}
