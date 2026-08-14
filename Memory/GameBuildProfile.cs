using System.Diagnostics;
using System.Security.Cryptography;

namespace Ap.Control.Memory
{
    /// <summary>
    /// Every address the client uses that depends on the exact game executable, in one place.
    ///
    /// Control ships a different build per storefront, and each storefront patches on its own
    /// schedule, so these RVAs shift between installs. Everything else the client touches is found by
    /// content — GameFlow variables by name CRC, the save chunk by signature — and is build-agnostic;
    /// only the three native granters need this table.
    ///
    /// Profiles are keyed on the SHA-256 of Control_DX12.exe rather than on the storefront, because
    /// "which launcher" stops implying "which addresses" the first time either store patches. The
    /// storefront is only ever used to label an error message.
    /// </summary>
    public sealed record GameBuildProfile
    {
        /// <summary>How many ability-point milestone thresholds <see cref="NativeAbilityGranter"/> expects.</summary>
        public const int MilestoneLevels = 3;

        /// <summary>Human-readable build name, e.g. "Steam 0.0.518.2177".</summary>
        public required string Name { get; init; }

        /// <summary>
        /// SHA-256 of Control_DX12.exe, uppercase hex. An empty string marks a profile whose binary
        /// has not been identified yet — the registry will not match on it.
        /// </summary>
        public required string Sha256 { get; init; }

        // --- Control_DX12.exe RVAs (from image base 0x140000000) ---------------------------------

        /// <summary>GameInventoryComponentState vtable.</summary>
        public required long InventoryVtable { get; init; }

        /// <summary>FUN_1403b6c30(this, char fire, GID* def, float amount) -> spawned object.</summary>
        public required long GiveItemFromDefinition { get; init; }

        /// <summary>UIHud vtable (owns the elevator panel's 5-bool Coherent model).</summary>
        public required long UiHudVtable { get; init; }

        /// <summary>*(*(this)+0x30) = PlayerPropertiesComponentState, the ability-tree manager.</summary>
        public required long AbilityMgrSlot { get; init; }

        /// <summary>AbilityTree_FireApplyPin(FlowConnMgr, pin, GID*, arg*).</summary>
        public required long FireApplyPin { get; init; }

        /// <summary>FUN_14006a030(FlowConnMgr, pin) — the generic output-pin fire.</summary>
        public required long FirePin { get; init; }

        /// <summary>*(*(holder)) = the FlowConnectionManager singleton.</summary>
        public required long FlowConnMgrHolder { get; init; }

        /// <summary>*(thunk) = coregame::GameHelper::saveGame(NetworkRole, 0, 0).</summary>
        public required long SaveGameThunk { get; init; }

        /// <summary>P = *(exe+rva); GameObjectManager(role) = *(P + roleOffset).</summary>
        public required long GomContainer { get; init; }

        /// <summary>
        /// Ability-point milestone thresholds (runtime ints), ascending: weapon slot, first extra mod
        /// slot, second extra mod slot. Must hold exactly <see cref="MilestoneLevels"/> entries.
        /// </summary>
        public required IReadOnlyList<long> MilestoneThresholds { get; init; }

        // --- coregame_rmdwin10_f.dll RVA ---------------------------------------------------------

        /// <summary>coregame::DynamicEntitySpawner::update — the per-frame main-thread pump we detour.</summary>
        public required long CoregamePump { get; init; }

        /// <summary>True once every address is filled in and the profile can actually be used.</summary>
        public bool IsComplete => MissingAddresses().Count == 0;

        /// <summary>
        /// Names of the addresses still left at zero (plus the milestone list if it is the wrong
        /// length). Used to turn a half-filled stub profile into an actionable error rather than a
        /// crash somewhere deep in a scan.
        /// </summary>
        public IReadOnlyList<string> MissingAddresses()
        {
            var missing = new List<string>();
            void Check(long rva, string name) { if (rva == 0) missing.Add(name); }

            Check(InventoryVtable, nameof(InventoryVtable));
            Check(GiveItemFromDefinition, nameof(GiveItemFromDefinition));
            Check(UiHudVtable, nameof(UiHudVtable));
            Check(AbilityMgrSlot, nameof(AbilityMgrSlot));
            Check(FireApplyPin, nameof(FireApplyPin));
            Check(FirePin, nameof(FirePin));
            Check(FlowConnMgrHolder, nameof(FlowConnMgrHolder));
            Check(SaveGameThunk, nameof(SaveGameThunk));
            Check(GomContainer, nameof(GomContainer));
            Check(CoregamePump, nameof(CoregamePump));

            if (MilestoneThresholds.Count != MilestoneLevels)
                missing.Add($"{nameof(MilestoneThresholds)} (expected {MilestoneLevels}, has {MilestoneThresholds.Count})");
            else
                for (int i = 0; i < MilestoneThresholds.Count; i++)
                    Check(MilestoneThresholds[i], $"{nameof(MilestoneThresholds)}[{i}]");

            return missing;
        }
    }

    /// <summary>
    /// Thrown when the running Control_DX12.exe is not a build this client has addresses for. Carries
    /// a message meant to be shown to the player verbatim.
    /// </summary>
    public sealed class UnsupportedGameBuildException : Exception
    {
        public UnsupportedGameBuildException(string message) : base(message) { }
    }

    /// <summary>
    /// Identifies the running game by hashing its executable and hands back the matching
    /// <see cref="GameBuildProfile"/>. Hashes are cached per file identity, so repeated lookups from
    /// the three granters cost one read of the 20 MB image, not three.
    /// </summary>
    public static class GameBuildRegistry
    {
        // ==========================================================================================
        //  Known builds.
        //
        //  To add one: hash the executable, then fill in the RVAs.
        //      powershell -c "(Get-FileHash '<game>\Control_DX12.exe').Hash"
        // ==========================================================================================

        /// <summary>
        /// Steam. Verified against Control_DX12.exe FileVersion 0.0.518.2177.
        /// </summary>
        public static readonly GameBuildProfile Steam = new()
        {
            Name = "Steam 0.0.518.2177",
            Sha256 = "9441DB3AE75B267ABD989846AD0895E3FE24ABCC0F06E58F93C34FC8D4736506",

            InventoryVtable        = 0x0E28A18,
            GiveItemFromDefinition = 0x03B6C30,
            UiHudVtable            = 0x0E5E5E8,
            AbilityMgrSlot         = 0x1239360,
            FireApplyPin           = 0x0211490,
            FirePin                = 0x006A030,
            FlowConnMgrHolder      = 0x0DA1058,
            SaveGameThunk          = 0x0D9C438,
            GomContainer           = 0x0DA10B0,
            MilestoneThresholds    = new long[] { 0x12B00A0, 0x12AFFE0, 0x12AFF20 },

            CoregamePump           = 0x007E0A0,
        };

        /// <summary>
        /// Epic Games Store. Verified against Control_DX12.exe FileVersion 0.0.518.2177.
        /// </summary>
        public static readonly GameBuildProfile Epic = new()
        {
            Name = "Epic Games Store 0.0.518.2177",
            Sha256 = "E1D11616941FAD767B20CD0FB1AB442771A912FF34EF404B5FB95136F53B6175",

            InventoryVtable        = 0x0E2D468,
            GiveItemFromDefinition = 0x03B8C50,
            UiHudVtable            = 0x0E62D80,
            AbilityMgrSlot         = 0x1240420,
            FireApplyPin           = 0x02134B0,
            FirePin                = 0x006C050,
            FlowConnMgrHolder      = 0x0DA50D8,
            SaveGameThunk          = 0x0DA0450,
            GomContainer           = 0x0DA5130,
            MilestoneThresholds    = new long[] { 0x12B8F60, 0x12B8EA0, 0x12B8DE0 },

            CoregamePump           = 0x007E0E0,
        };

        /// <summary>
        /// GOG. Verified against Control_DX12.exe FileVersion 0.0.518.2177.
        /// </summary>
        public static readonly GameBuildProfile Gog = new()
        {
            Name = "GOG 0.0.518.2177",
            Sha256 = "57A8912F1FD839E99162AED2536914DEE01FF298938132354DA579ADC690E91E",

            InventoryVtable        = 0x0E28A18,
            GiveItemFromDefinition = 0x03B6C30,
            UiHudVtable            = 0x0E5E5E8,
            AbilityMgrSlot         = 0x1239360,
            FireApplyPin           = 0x0211490,
            FirePin                = 0x006A030,
            FlowConnMgrHolder      = 0x0DA1058,
            SaveGameThunk          = 0x0D9C438,
            GomContainer           = 0x0DA10B0,
            MilestoneThresholds    = new long[] { 0x12B00A0, 0x12AFFE0, 0x12AFF20 },

            CoregamePump           = 0x007E0A0,
        };

        /// <summary>Every profile the client ships, mapped or not.</summary>
        public static IReadOnlyList<GameBuildProfile> All { get; } = new[] { Steam, Epic, Gog };

        // ==========================================================================================

        private const string ProcessName = "Control_DX12";

        private static readonly object Gate = new();
        private static (string Path, long Length, DateTime WriteUtc, string Hash)? _cachedHash;

        /// <summary>
        /// Identify the build backing <paramref name="proc"/>.
        /// </summary>
        /// <exception cref="UnsupportedGameBuildException">
        /// The executable's hash matches no mapped profile, or matches a profile that is still a stub.
        /// </exception>
        public static GameBuildProfile Resolve(Process proc)
        {
            string exePath = proc.MainModule?.FileName
                ?? throw new InvalidOperationException("Could not read the game's executable path.");

            string hash = HashExecutable(exePath);

            foreach (GameBuildProfile p in All)
            {
                if (p.Sha256.Length == 0
                    || !string.Equals(p.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!p.IsComplete)
                    throw new UnsupportedGameBuildException(
                        $"the {p.Name} build is recognised but not fully mapped — missing "
                        + string.Join(", ", p.MissingAddresses()) + ".");

                return p;
            }

            throw new UnsupportedGameBuildException(DescribeUnknown(exePath, hash));
        }

        /// <summary>
        /// <see cref="Resolve(Process)"/> for the running game, or null if Control_DX12 is not up.
        /// Still throws <see cref="UnsupportedGameBuildException"/> for a running-but-unknown build.
        /// </summary>
        public static GameBuildProfile? ResolveRunning()
        {
            using Process? proc = MemoryHelper.TryGetProcess(ProcessName);
            return proc is null ? null : Resolve(proc);
        }

        /// <summary>
        /// One line (or short block) for the client's startup output: which build is running, or why
        /// the build-specific features will not work. Never throws.
        /// </summary>
        public static string StartupBanner()
        {
            try
            {
                GameBuildProfile? p = ResolveRunning();
                return p is null
                    ? $"Game build: {ProcessName} is not running yet — it will be identified on the first grant."
                    : $"Game build: {p.Name} (matched by executable hash).";
            }
            catch (UnsupportedGameBuildException e)
            {
                return "[warning] Game build: " + e.Message + Environment.NewLine
                    + "          Clearance and sector/door flags will still work (they are found by name, "
                    + "not by address)." + Environment.NewLine
                    + "          Inventory items, ability upgrades and the elevator panel UI will not.";
            }
            catch (Exception e)
            {
                return $"[warning] Game build: could not be identified ({e.Message}).";
            }
        }

        /// <summary>The storefront an install path implies. Used only to make an error message useful.</summary>
        public static string DetectStorefront(string exePath)
        {
            string p = exePath.Replace('/', '\\');
            bool Has(string s) => p.Contains(s, StringComparison.OrdinalIgnoreCase);

            if (Has(@"\steamapps\")) return "Steam";
            if (Has(@"\Epic Games\") || Has(@"\EpicGames\")) return "Epic Games Store";
            if (Has("GOG")) return "GOG";
            if (Has(@"\WindowsApps\") || Has(@"\XboxGames\")) return "Microsoft Store / Game Pass";
            return "unknown storefront";
        }

        private static string DescribeUnknown(string exePath, string hash)
        {
            string storefront = DetectStorefront(exePath);
            string version = "unknown version", size = "unknown size";
            try
            {
                var fi = new FileInfo(exePath);
                size = $"{fi.Length:N0} bytes";
                version = FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? version;
            }
            catch { /* the description is best-effort; the hash is the part that matters */ }

            string known = string.Join(", ", All.Where(p => p.Sha256.Length > 0 && p.IsComplete).Select(p => p.Name));
            if (known.Length == 0) known = "(none)";

            return $"unsupported game build. {ProcessName}.exe looks like a {storefront} install "
                + $"(FileVersion {version}, {size}) with SHA-256 {hash}, which matches no profile in this client. "
                + $"Mapped builds: {known}.";
        }

        /// <summary>SHA-256 of a file as uppercase hex, cached by path/size/timestamp.</summary>
        public static string HashExecutable(string path)
        {
            var fi = new FileInfo(path);
            long length = fi.Length;
            DateTime writeUtc = fi.LastWriteTimeUtc;

            lock (Gate)
            {
                if (_cachedHash is { } c
                    && string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)
                    && c.Length == length && c.WriteUtc == writeUtc)
                    return c.Hash;
            }

            // Read-share the file: the game has it open and mapped while it runs.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            string hash = Convert.ToHexString(SHA256.HashData(fs));

            lock (Gate) _cachedHash = (path, length, writeUtc, hash);
            return hash;
        }
    }
}
