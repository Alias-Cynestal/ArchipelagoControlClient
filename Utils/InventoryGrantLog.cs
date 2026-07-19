using System.Text.Json;

namespace Ap.Control.Utils
{
    public sealed class InventoryGrantLog
    {
        private readonly string? _path;
        private readonly string _seed;
        private readonly int _slot;
        private readonly SortedSet<int> _granted;

        /// <summary>Where the log persists, or null when persistence is unavailable (in-memory only).</summary>
        public string? Path => _path;

        /// <summary>How many inventory grants have been performed for this seed/slot.</summary>
        public int Count => _granted.Count;

        private InventoryGrantLog(string? path, string seed, int slot, SortedSet<int> granted)
        {
            _path = path;
            _seed = seed;
            _slot = slot;
            _granted = granted;
        }

        /// <summary>An in-memory-only log, for when the state directory can't be used.</summary>
        public static InventoryGrantLog InMemory(string seed, int slot) =>
            new(null, seed, slot, new SortedSet<int>());

        /// <summary>True if the item at <paramref name="ordinal"/> has already been granted.</summary>
        public bool IsGranted(int ordinal) => _granted.Contains(ordinal);

        /// <summary>
        /// Record that the item at <paramref name="ordinal"/> was granted into the game, persisting
        /// immediately so an abrupt exit can't lose the record and duplicate on the next connect.
        /// </summary>
        public void MarkGranted(int ordinal)
        {
            if (!_granted.Add(ordinal))
                return;
            if (_path is null)
                return;
            try
            {
                Save();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[grant-log] could not persist to {_path}: {e.Message} "
                    + "(grants stay correct this session; a reconnect may duplicate)");
            }
        }

        /// <summary>
        /// Load the log for <paramref name="seed"/>/<paramref name="slot"/> from <paramref name="dir"/>, creating the directory if needed.
        /// </summary>
        public static InventoryGrantLog Load(string dir, string seed, int slot)
        {
            string path = System.IO.Path.Combine(dir, FileNameFor(seed, slot));
            Directory.CreateDirectory(dir);

            if (!File.Exists(path))
                return new InventoryGrantLog(path, seed, slot, new SortedSet<int>());

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                string? storedSeed = root.TryGetProperty("seed", out var s) ? s.GetString() : null;
                int storedSlot = root.TryGetProperty("slot", out var sl) ? sl.GetInt32() : -1;
                if (storedSeed != seed || storedSlot != slot)
                {
                    Console.Error.WriteLine($"[grant-log] {path} is for a different seed/slot — starting empty.");
                    return new InventoryGrantLog(path, seed, slot, new SortedSet<int>());
                }

                var granted = new SortedSet<int>();
                if (root.TryGetProperty("granted", out var g) && g.ValueKind == JsonValueKind.Array)
                    foreach (var o in g.EnumerateArray())
                        granted.Add(o.GetInt32());

                return new InventoryGrantLog(path, seed, slot, granted);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[grant-log] could not read {path}: {e.Message} — starting empty.");
                return new InventoryGrantLog(path, seed, slot, new SortedSet<int>());
            }
        }

        private void Save()
        {
            string json = JsonSerializer.Serialize(new
            {
                seed = _seed,
                slot = _slot,
                granted = _granted.ToArray(),
            });

            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path!, overwrite: true);
        }

        private static string FileNameFor(string seed, int slot)
        {
            var safe = seed.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
            return $"grants-{slot}-{new string(safe)}.json";
        }
    }
}
