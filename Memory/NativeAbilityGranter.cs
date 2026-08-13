using System.Diagnostics;
using System.Runtime.InteropServices;
using Ap.Control.Models;
using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Memory
{
    /// <summary>
    /// Grants ability-tree upgrades on the game's own main thread.
    ///
    /// The ability apply/enumerate functions fire live flow-graph pins, which are only valid on the
    /// main thread at a clean frame boundary — calling them from a foreign thread (CreateRemoteThread)
    /// crashes the game, and suspending the other threads doesn't help (the code still isn't running
    /// in the main thread's flow context). So instead this installs a small detour on the per-frame
    /// pump <c>coregame::DynamicEntitySpawner::update</c> and uses it as a main-thread RPC executor:
    /// each frame the hook checks a shared control block and, if a request is pending, calls
    /// <c>fn(rcx,rdx,r8,r9)</c> and stores the return — on the real main thread. The C# side drives the
    /// whole grant sequence as a series of these one-call-per-frame requests.
    ///
    /// PHASE 1 (this version): the hook mechanism + a non-mutating probe (enumerate the live upgrade
    /// instances and resolve each to its definition GID). The mutating grant path is not enabled yet.
    /// See Memory/ability-upgrade-menu findings for the full trace.
    /// </summary>
    public sealed class NativeAbilityGranter : IAbilityGranter
    {
        // --- Build-specific addresses ---------------------------------------------------------------
        // Control_DX12.exe RVAs (image base 0x140000000):
        private const long RVA_MGR_SLOT           = 0x1239360; // *(*(this)+0x30) = PlayerPropertiesComponentState (mgr)
        private const int  MGR_OFF_INSTANCE        = 0x30;
        private const int  MGR_OFF_OWNER           = 0x28;     // owner entity ptr; FireApplyPin arg = *(owner)+0x18
        private const int  MGR_OFF_PIN             = 0xf8;     // apply output-pin handle (FireApplyPin's rdx = mgr+0xf8)
        private const int  MGR_OFF_ROLE_SRC        = 0x18;     // top 2 bits = NetworkRole (for the post-grant saveGame)

        private const long RVA_FIRE_APPLY_PIN      = 0x211490; // AbilityTree_FireApplyPin(FlowConnMgr, pin, GID*, arg*) — direct
        private const long RVA_FLOWCONNMGR_HOLDER  = 0xda1058; // *(*(holder)) = FlowConnectionManager singleton
        private const long RVA_SAVEGAME_THUNK      = 0xd9c438; // coregame::GameHelper::saveGame(NetworkRole,0,0) — *(thunk) is the fn

        // Ability-point milestone rewards (weapon slot / 2 mod slots). Gated on the spent-points high-
        // water mark mgr+0x50 vs three runtime thresholds; the reward is applied by firing the milestone
        // output pin mgr+0x120 with the generic pin-fire FUN_14006a030(FlowConnMgr, pin).
        private const int  MGR_OFF_SPENT_HIGHWATER = 0x50;    // int; compared against the milestone thresholds
        private const int  MGR_OFF_MILESTONE_PIN   = 0x120;   // milestone reward output-pin handle
        private const long RVA_FIRE_PIN            = 0x6a030; // FUN_14006a030(FlowConnMgr, pin) — direct, fires an output pin
        // Milestone thresholds (runtime ints, populated when the ability tree loads), ascending:
        private static readonly long[] MilestoneThresholdRvas =
        {
            0x12b00a0, // ABILITY_MILESTONE_WEAPON_SLOT
            0x12affe0, // ABILITY_MILESTONE_EXTRA_MOD_SLOT (first)
            0x12aff20, // ABILITY_MILESTONE_EXTRA_MOD_SLOT (second; also the progress-bar max)
        };

        // GameObjectManager entity-table scan (menu-free enumeration — pure reads, no calls):
        private const long RVA_GOM_CONTAINER   = 0xda10b0;  // P = *(exe+0xda10b0); GOM(role) = *(P + roleOffset)
        private static readonly int[] GomRoleOffsets = { -8, 0, 8 };
        private const long GOM_TABLE           = 0x310;     // entity pointer array
        private const int  GOM_TABLE_SLOTS     = 0x20000;   // index masked with 0x1ffff -> 131072 slots
        private const int  ENT_INSTANCE_GID    = 0x18;      // EntityState+0x18 = runtime instance GID
        private const int  ENT_ARCHETYPE_GID   = 0x80;      // EntityState+0x80 = archetype (definition) GID
        private const uint GID_TYPE_MASK       = 0x3fff;    // low 14 bits = content type tag
        private const uint TYPE_ABILITY_UPGRADE = 77;       // ability_upgrades\* content type (0x4D)

        // coregame_rmdwin10_f.dll RVA:
        private const long RVA_PUMP                = 0x7e0a0;  // coregame::DynamicEntitySpawner::update (per-frame, main thread)

        private const string ProcessName    = "Control_DX12";
        private const string CoregameModule  = "coregame_rmdwin10_f.dll";
        private const int PROCESS_ACCESS = 0x0008 /*VM_OPERATION*/ | 0x0010 /*VM_READ*/ | 0x0020 /*VM_WRITE*/
                                          | 0x0002 /*CREATE_THREAD*/ | 0x0400 /*QUERY_INFORMATION*/;

        // --- Control block layout (offsets from _ctrl) ---------------------------------------------
        private const int CB_FN      = 0x00;
        private const int CB_A0      = 0x08;
        private const int CB_A1      = 0x10;
        private const int CB_A2      = 0x18;
        private const int CB_A3      = 0x20;
        private const int CB_RESULT  = 0x28;
        private const int CB_PENDING = 0x30;  // 0 idle, 1 request, 2 done
        private const int CB_HEARTBEAT = 0x38; // incremented by the hook on every pump tick (liveness)
        private const int CB_GID     = 0x40;  // scratch: 8-byte GID in
        private const int CB_ARCH    = 0x50;  // scratch: 24-byte GlobalIDPointer out
        private const int CB_VEC     = 0x70;  // scratch: 24-byte ScratchPad vector out
        private const int CB_SIZE    = 0x100;

        private const long PUMP_STOLEN = 0x0F; // bytes of prologue relocated into the trampoline

        private IntPtr _hProc;
        private IntPtr _imageBase;
        private long   _coregameBase;
        private int    _pid;

        // Hook state
        private readonly object _hookLock = new();
        private readonly object _rpcLock = new();
        private IntPtr _ctrl;         // control block (RW)
        private IntPtr _codePage;     // hook + trampoline + self-test stub (RX)
        private long   _selfTestStub; // absolute address of a trivial "mov eax,0xC0FFEE; ret" stub
        private long   _pumpEntry;    // absolute address of the patched pump entry
        private byte[]? _origPumpBytes;
        private bool   _hookInstalled;

        private const uint SelfTestMagic = 0x00C0FFEE;

        public bool IsReady => _hookInstalled;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCore();
            return Task.CompletedTask;
        }

        private void StartCore()
        {
            if (_hProc != IntPtr.Zero) return;

            Process proc = MemoryHelper.GetProcessOrThrow(ProcessName);
            _imageBase = proc.MainModule?.BaseAddress
                ?? throw new InvalidOperationException("Could not read the game's image base.");
            _pid = proc.Id;

            foreach (ProcessModule m in proc.Modules)
                if (string.Equals(m.ModuleName, CoregameModule, StringComparison.OrdinalIgnoreCase))
                { _coregameBase = m.BaseAddress.ToInt64(); break; }
            if (_coregameBase == 0)
                throw new InvalidOperationException($"{CoregameModule} not loaded — is a save running?");

            _hProc = MemoryHelper.OpenProcessHandle(PROCESS_ACCESS, false, proc.Id);
            if (_hProc == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"OpenProcess failed (error {Marshal.GetLastWin32Error()}). Try running as administrator.");
        }

        private bool EnsureStarted()
        {
            if (_hProc != IntPtr.Zero) return true;
            try { StartCore(); } catch { /* game not running / not openable yet */ }
            return _hProc != IntPtr.Zero;
        }

        // ===========================================================================================
        //  Phase-1 probe: enumerate live upgrade instances and resolve each to its definition GID.
        //  All read-only — no flow-pin firing, no point/inventory mutation.
        // ===========================================================================================

        /// <summary>One resolved live ability-upgrade node: its runtime instance GID and definition GID.</summary>
        public readonly record struct LiveUpgrade(ulong InstanceGid, ulong DefinitionGid);

        /// <summary>
        /// A scan of the entity table, with enough of the shape of what was walked to tell the three
        /// zero-result cases apart: the table was unreadable (wrong build / bad addresses), it was
        /// readable but empty (no save loaded), or it was populated and the target simply is not in it.
        /// </summary>
        private readonly record struct UpgradeScan(
            List<LiveUpgrade> Upgrades, int Goms, int Entities, int SlotsRead);

        /// <summary>
        /// Menu-free enumeration: walk the GameObjectManager entity table(s) and collect every live
        /// entity whose archetype is a type-77 <c>ability_upgrades\*</c> definition, pairing each
        /// instance GID with its definition GID. Pure reads — no game calls, no pump, nothing mutated.
        /// </summary>
        private UpgradeScan ScanAbilityUpgradeInstances(bool verbose)
        {
            long container = (long)ReadChecked(_imageBase.ToInt64() + RVA_GOM_CONTAINER, "GOM container (exe+0xda10b0)");
            if (verbose) Console.WriteLine($"  GOM container = 0x{container:X}");

            var result = new List<LiveUpgrade>();
            var seenGom = new HashSet<long>();
            var seenInst = new HashSet<ulong>();
            var ptrArray = new byte[0x10000 * 8];   // 64k pointers per chunk
            var scratch8 = new byte[8];
            int goms = 0, entities = 0, slotsRead = 0;

            foreach (int off in GomRoleOffsets)
            {
                byte[]? gb = MemoryHelper.TryReadExact(_hProc, (IntPtr)(container + off), 8);
                if (gb is null) continue;
                long gom = BitConverter.ToInt64(gb, 0);
                if (gom == 0 || !seenGom.Add(gom)) continue;
                goms++;

                long tableBase = gom + GOM_TABLE;
                int nonNull = 0, matched = 0;
                for (int s = 0; s < GOM_TABLE_SLOTS;)
                {
                    int take = Math.Min(0x10000, GOM_TABLE_SLOTS - s);
                    if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(tableBase + (long)s * 8), ptrArray, take * 8, out _))
                        break;   // table shorter than the mask allows, or unmapped tail
                    slotsRead += take;
                    for (int j = 0; j < take; j++)
                    {
                        long e = BitConverter.ToInt64(ptrArray, j * 8);
                        if (e == 0) continue;
                        nonNull++;

                        if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(e + ENT_ARCHETYPE_GID), scratch8, 8, out _))
                            continue;
                        ulong arche = BitConverter.ToUInt64(scratch8, 0);
                        if ((arche & GID_TYPE_MASK) != TYPE_ABILITY_UPGRADE) continue;

                        if (!MemoryHelper.TryReadBytes(_hProc, (IntPtr)(e + ENT_INSTANCE_GID), scratch8, 8, out _))
                            continue;
                        ulong inst = BitConverter.ToUInt64(scratch8, 0);
                        if (!seenInst.Add(inst)) continue;

                        result.Add(new LiveUpgrade(inst, arche));
                        matched++;
                    }
                    s += take;
                }
                entities += nonNull;
                if (verbose)
                    Console.WriteLine($"  GOM(off {off,2}) = 0x{gom:X}: {nonNull} live entities, {matched} type-77 ability upgrade(s)");
            }
            return new UpgradeScan(result, goms, entities, slotsRead);
        }

        /// <summary>
        /// A full table walk costs two reads per live entity, and a stalled grant queue retries every
        /// held ability once a second — without this, one connect at the main menu turns into dozens of
        /// scans per tick. The window is short enough that a save loading is picked up on the next pass.
        /// </summary>
        private const int ScanCacheMs = 500;
        private readonly object _scanLock = new();
        private UpgradeScan _scanCache;
        private DateTime _scanCacheUtc = DateTime.MinValue;

        private UpgradeScan ScanCached(bool verbose)
        {
            lock (_scanLock)
            {
                if (!verbose && _scanCache.Upgrades is not null
                    && DateTime.UtcNow - _scanCacheUtc < TimeSpan.FromMilliseconds(ScanCacheMs))
                    return _scanCache;

                UpgradeScan scan = ScanAbilityUpgradeInstances(verbose);
                _scanCache = scan;
                _scanCacheUtc = DateTime.UtcNow;
                return scan;
            }
        }

        /// <summary>Why a definition resolved to nothing, in terms of what the scan actually saw.</summary>
        private static string DescribeMiss(ulong definitionGid, in UpgradeScan scan)
        {
            string what = $"no live instance for definition 0x{definitionGid:X}";
            if (scan.Goms == 0 || scan.SlotsRead == 0)
                return $"{what} — the entity table could not be read at all "
                    + $"({scan.Goms} manager(s), {scan.SlotsRead} slot(s) read); the game's addresses may not "
                    + "match this build.";
            if (scan.Entities == 0)
                return $"{what} — the entity table is empty ({scan.SlotsRead} slots scanned), so no save is loaded.";
            if (scan.Upgrades.Count == 0)
                return $"{what} — {scan.Entities} live entities but no ability-upgrade entities at all; "
                    + "the ability tree is not loaded yet.";
            return $"{what} — not among the {scan.Upgrades.Count} upgrade instance(s) spawned in this save "
                + $"({scan.Entities} live entities scanned).";
        }

        /// <summary>ReadProcessMemory of 8 bytes that reports which labelled read faulted, and where.</summary>
        private ulong ReadChecked(long addr, string label)
        {
            byte[]? b = MemoryHelper.TryReadExact(_hProc, (IntPtr)addr, 8);
            if (b is null)
                throw new InvalidOperationException($"read failed reading {label} at 0x{addr:X}");
            return BitConverter.ToUInt64(b, 0);
        }

        public Task<GrantResult> GrantAbilityAsync(ulong definitionGid, CancellationToken cancellationToken = default)
            => Task.Run(() => GrantAbility(definitionGid, verbose: false), cancellationToken);

        /// <summary>
        /// Grant one ability upgrade by its definition GID: find its live instance(s) in the entity
        /// table, then fire the ability-tree apply pin on the game's main thread (via the pump hook) —
        /// the point-cost-free path that <c>ApplyUpgrade</c> uses internally. Tries each replica until
        /// one reports success, then saves. Returns rejected if no live instance exists for the def
        /// (not currently spawned) or none of the replicas accepted.
        /// </summary>
        public GrantResult GrantAbility(ulong definitionGid, bool verbose)
        {
            if (!EnsureStarted()) return GrantResult.Fail("granter not started — is Control_DX12 running?");

            UpgradeScan scan;
            List<ulong> instances;
            try
            {
                scan = ScanCached(verbose);
                instances = scan.Upgrades
                    .Where(u => u.DefinitionGid == definitionGid)
                    .Select(u => u.InstanceGid)
                    .Distinct()
                    .ToList();
            }
            catch (Exception e) { return GrantResult.Fail($"instance scan failed: {e.Message}"); }

            if (instances.Count == 0)
                return GrantResult.Fail(DescribeMiss(definitionGid, scan));

            if (verbose) Console.WriteLine($"  {instances.Count} instance(s) for 0x{definitionGid:X}: "
                + string.Join(", ", instances.Select(i => $"0x{i:X}")));

            try
            {
                EnsureHookInstalled();

                long container = (long)ReadChecked(_imageBase.ToInt64() + RVA_MGR_SLOT, "mgr container (exe+0x1239360)");
                long mgr = container == 0 ? 0 : (long)ReadPtr(container + MGR_OFF_INSTANCE);
                if (mgr == 0) return GrantResult.Fail("ability-tree manager not resolved — is a save loaded?");

                long flowHolder = (long)ReadPtr(_imageBase.ToInt64() + RVA_FLOWCONNMGR_HOLDER);
                long flowMgr = flowHolder == 0 ? 0 : (long)ReadPtr(flowHolder);
                long owner = (long)ReadPtr(mgr + MGR_OFF_OWNER);
                ulong arg = owner == 0 ? 0UL : MemoryHelper.ReadU64(_hProc, (IntPtr)(owner + 0x18));
                if (flowMgr == 0) return GrantResult.Fail("FlowConnectionManager not resolved.");

                long fireFn = _imageBase.ToInt64() + RVA_FIRE_APPLY_PIN;
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(_ctrl.ToInt64() + CB_ARCH), BitConverter.GetBytes(arg));

                bool applied = false;
                foreach (ulong inst in instances)
                {
                    MemoryHelper.WriteBytes(_hProc, (IntPtr)(_ctrl.ToInt64() + CB_GID), BitConverter.GetBytes(inst));
                    ulong ret = MainThreadCall(fireFn, flowMgr, mgr + MGR_OFF_PIN,
                        _ctrl.ToInt64() + CB_GID, _ctrl.ToInt64() + CB_ARCH);
                    bool ok = (ret & 0xFF) != 0;
                    if (verbose) Console.WriteLine($"  FireApplyPin(0x{inst:X}) -> {(ok ? "accepted" : "no-op")}");
                    if (ok) { applied = true; break; }
                }

                if (applied) SaveGame(mgr);

                return new GrantResult { Ok = true, Accepted = applied };
            }
            catch (Exception e) { return GrantResult.Fail($"grant failed: {e.Message}"); }
        }

        public Task<GrantResult> GrantMilestoneAsync(int level, CancellationToken cancellationToken = default)
            => Task.Run(() => GrantMilestone(level, verbose: false), cancellationToken);

        /// <summary>
        /// Grant the ability-point milestone rewards up to <paramref name="level"/> (1 = weapon slot,
        /// 2 = +first mod slot, 3 = +second mod slot). Milestones are cumulative — a single spent-points
        /// high-water counter (mgr+0x50) crossing runtime thresholds — so this raises that counter to the
        /// level's threshold and fires the milestone reward pin (mgr+0x120) on the main thread, which
        /// grants every perk unlocked at that level. Intended to be driven progressively: the Nth
        /// progressive-milestone AP item received calls this with level N.
        /// </summary>
        public GrantResult GrantMilestone(int level, bool verbose)
        {
            if (level < 1 || level > MilestoneThresholdRvas.Length)
                return GrantResult.Fail($"milestone level {level} out of range (1..{MilestoneThresholdRvas.Length})");
            if (!EnsureStarted()) return GrantResult.Fail("granter not started — is Control_DX12 running?");

            try
            {
                EnsureHookInstalled();

                long container = (long)ReadChecked(_imageBase.ToInt64() + RVA_MGR_SLOT, "mgr container (exe+0x1239360)");
                long mgr = container == 0 ? 0 : (long)ReadPtr(container + MGR_OFF_INSTANCE);
                if (mgr == 0) return GrantResult.Fail("ability-tree manager not resolved — is a save loaded?");

                long flowHolder = (long)ReadPtr(_imageBase.ToInt64() + RVA_FLOWCONNMGR_HOLDER);
                long flowMgr = flowHolder == 0 ? 0 : (long)ReadPtr(flowHolder);
                if (flowMgr == 0) return GrantResult.Fail("FlowConnectionManager not resolved.");

                int threshold = MemoryHelper.ReadI32(_hProc, (IntPtr)(_imageBase.ToInt64() + MilestoneThresholdRvas[level - 1]));
                if (threshold <= 0)
                    return GrantResult.Fail(
                        $"milestone threshold for level {level} reads {threshold} — not loaded yet (in active gameplay?)");

                // Raise the spent-points high-water mark to the threshold (never lower it), so the reward
                // pin's flow node sees the milestone as reached. Points AVAILABLE (mgr+0x48) is untouched,
                // so this does not hand the player anything to spend.
                int current = MemoryHelper.ReadI32(_hProc, (IntPtr)(mgr + MGR_OFF_SPENT_HIGHWATER));
                if (current < threshold)
                    MemoryHelper.WriteI32(_hProc, (IntPtr)(mgr + MGR_OFF_SPENT_HIGHWATER), threshold);
                if (verbose) Console.WriteLine($"  level {level}: threshold={threshold}, spent-highwater {current} -> "
                    + $"{Math.Max(current, threshold)}");

                MainThreadCall(_imageBase.ToInt64() + RVA_FIRE_PIN, flowMgr, mgr + MGR_OFF_MILESTONE_PIN, 0, 0);
                if (verbose) Console.WriteLine("  fired milestone reward pin (mgr+0x120)");

                SaveGame(mgr);
                return new GrantResult { Ok = true, Accepted = true };
            }
            catch (Exception e) { return GrantResult.Fail($"milestone grant failed: {e.Message}"); }
        }

        /// <summary>Persist, matching ApplyUpgrade's own success path (role from mgr+0x18 top 2 bits).</summary>
        private void SaveGame(long mgr)
        {
            ulong roleRaw = MemoryHelper.ReadU64(_hProc, (IntPtr)(mgr + MGR_OFF_ROLE_SRC));
            int top2 = (int)(roleRaw >> 62);
            long role = top2 == 2 ? 1 : top2 == 3 ? 2 : 0;
            long saveFn = (long)ReadPtr(_imageBase.ToInt64() + RVA_SAVEGAME_THUNK);
            if (saveFn != 0) { try { MainThreadCall(saveFn, role, 0, 0, 0); } catch { /* change already applied */ } }
        }

        // ===========================================================================================
        //  Main-thread RPC: write a request into the control block, let the pump hook run it, wait.
        // ===========================================================================================

        private ulong MainThreadCall(long fn, long a0, long a1, long a2, long a3, int timeoutMs = 5000)
        {
            lock (_rpcLock)
            {
                long c = _ctrl.ToInt64();
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(c + CB_FN), BitConverter.GetBytes(fn));
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(c + CB_A0), BitConverter.GetBytes(a0));
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(c + CB_A1), BitConverter.GetBytes(a1));
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(c + CB_A2), BitConverter.GetBytes(a2));
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(c + CB_A3), BitConverter.GetBytes(a3));
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(c + CB_RESULT), BitConverter.GetBytes(0L));
                // Publish the request last, after every arg is already in memory (x86 store order holds).
                ulong beatStart = MemoryHelper.ReadU64(_hProc, (IntPtr)(c + CB_HEARTBEAT));
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(c + CB_PENDING), BitConverter.GetBytes(1L));

                var sw = Stopwatch.StartNew();
                while (true)
                {
                    ulong pending = MemoryHelper.ReadU64(_hProc, (IntPtr)(c + CB_PENDING));
                    if (pending == 2) break;
                    if (sw.ElapsedMilliseconds > timeoutMs)
                    {
                        ulong beats = MemoryHelper.ReadU64(_hProc, (IntPtr)(c + CB_HEARTBEAT)) - beatStart;
                        ulong pendNow = MemoryHelper.ReadU64(_hProc, (IntPtr)(c + CB_PENDING));
                        ulong resNow = MemoryHelper.ReadU64(_hProc, (IntPtr)(c + CB_RESULT));
                        MemoryHelper.WriteBytes(_hProc, (IntPtr)(c + CB_PENDING), BitConverter.GetBytes(0L));
                        throw new InvalidOperationException(beats == 0
                            ? "main-thread call timed out — the pump hook never ticked in "
                              + $"{timeoutMs} ms (the hooked function isn't running: menu/pause, or not truly per-frame). "
                              + "Try moving the character during the grant."
                            : $"main-thread call timed out but the pump ticked {beats}x (pending={pendNow}, result=0x{resNow:X}) "
                              + "— the hook ran but the request wasn't serviced.");
                    }
                    Thread.Sleep(1);
                }

                ulong ret = MemoryHelper.ReadU64(_hProc, (IntPtr)(c + CB_RESULT));
                MemoryHelper.WriteBytes(_hProc, (IntPtr)(c + CB_PENDING), BitConverter.GetBytes(0L));
                return ret;
            }
        }

        // ===========================================================================================
        //  Detour install / uninstall.
        // ===========================================================================================

        private void EnsureHookInstalled()
        {
            lock (_hookLock)
            {
                if (_hookInstalled) return;

                _pumpEntry = _coregameBase + RVA_PUMP;

                _ctrl = VirtualAllocEx(_hProc, IntPtr.Zero, CB_SIZE, MEM_COMMIT_RESERVE, PAGE_READWRITE);
                if (_ctrl == IntPtr.Zero) throw new InvalidOperationException("VirtualAllocEx(ctrl) failed");
                MemoryHelper.WriteBytes(_hProc, _ctrl, new byte[CB_SIZE]);

                byte[] stolen = MemoryHelper.ReadBytes(_hProc, (IntPtr)_pumpEntry, (int)PUMP_STOLEN);
                // If the pump entry already starts with our jmp thunk, a previous run didn't clean up.
                // Installing over it would relocate the stale jmp into our trampoline and corrupt the
                // chain — bail with a clear message rather than silently misbehaving.
                if (stolen[0] == 0xFF && stolen[1] == 0x25)
                {
                    VirtualFreeEx(_hProc, _ctrl, 0, MEM_RELEASE); _ctrl = IntPtr.Zero;
                    throw new InvalidOperationException(
                        "the game's pump is already hooked (a stale hook from a previous run) — "
                        + "restart Control to clear it, then try again.");
                }
                _origPumpBytes = stolen;

                // Lay out the code page: [hookCode][trampoline].
                _codePage = VirtualAllocEx(_hProc, IntPtr.Zero, 0x200, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
                if (_codePage == IntPtr.Zero) throw new InvalidOperationException("VirtualAllocEx(code) failed");

                byte[] hookProbe = BuildHookCode(_ctrl.ToInt64(), 0); // length is address-independent
                long trampAddr = _codePage.ToInt64() + Align(hookProbe.Length, 16);
                byte[] trampoline = BuildTrampoline(stolen, _pumpEntry + PUMP_STOLEN);
                long stubAddr = trampAddr + Align(trampoline.Length, 16);
                byte[] hookCode = BuildHookCode(_ctrl.ToInt64(), trampAddr);

                MemoryHelper.WriteBytes(_hProc, _codePage, hookCode);
                MemoryHelper.WriteBytes(_hProc, (IntPtr)trampAddr, trampoline);
                FlushInstructionCache(_hProc, _codePage, (IntPtr)0x200);

                // Patch the pump entry to jump into the hook, with every game thread suspended so the
                // 15-byte write can't be observed half-applied.
                byte[] patch = BuildEntryPatch(_codePage.ToInt64(), (int)PUMP_STOLEN);
                var suspended = SuspendAllThreads();
                try
                {
                    MemoryHelper.WriteBytes(_hProc, (IntPtr)_pumpEntry, patch);
                    FlushInstructionCache(_hProc, (IntPtr)_pumpEntry, (IntPtr)PUMP_STOLEN);
                }
                finally { ResumeThreads(suspended); }

                _hookInstalled = true;
            }
        }

        private void UninstallHook()
        {
            lock (_hookLock)
            {
                if (!_hookInstalled) return;
                try
                {
                    if (_origPumpBytes is { } orig)
                    {
                        var suspended = SuspendAllThreads();
                        try
                        {
                            MemoryHelper.WriteBytes(_hProc, (IntPtr)_pumpEntry, orig);
                            FlushInstructionCache(_hProc, (IntPtr)_pumpEntry, (IntPtr)PUMP_STOLEN);
                        }
                        finally { ResumeThreads(suspended); }
                    }
                }
                catch { /* process may be gone already */ }
                finally
                {
                    // Only safe to free the code page after the entry no longer jumps into it.
                    if (_codePage != IntPtr.Zero) { try { VirtualFreeEx(_hProc, _codePage, 0, MEM_RELEASE); } catch { } _codePage = IntPtr.Zero; }
                    if (_ctrl != IntPtr.Zero) { try { VirtualFreeEx(_hProc, _ctrl, 0, MEM_RELEASE); } catch { } _ctrl = IntPtr.Zero; }
                    _hookInstalled = false;
                }
            }
        }

        private static int Align(int v, int a) => (v + a - 1) / a * a;

        // --- Shellcode builders --------------------------------------------------------------------

        /// <summary>
        /// The per-frame hook body. Runs at the pump entry with the original register state live, so it
        /// saves everything it touches, services one pending RPC request if present, restores, then
        /// jumps to the trampoline (which runs the stolen prologue and continues the real function).
        /// </summary>
        private static byte[] BuildHookCode(long ctrl, long trampoline)
        {
            var b = new List<byte>();
            b.Add(0x50);                                            // push rax
            b.Add(0x9C);                                            // pushfq
            b.Add(0x51);                                            // push rcx
            b.Add(0x52);                                            // push rdx
            b.AddRange(new byte[] { 0x41, 0x50 });                 // push r8
            b.AddRange(new byte[] { 0x41, 0x51 });                 // push r9
            b.AddRange(new byte[] { 0x41, 0x52 });                 // push r10
            b.AddRange(new byte[] { 0x41, 0x53 });                 // push r11
            b.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });     // sub rsp, 0x28   (16-align + shadow)
            b.AddRange(new byte[] { 0x49, 0xBB }); b.AddRange(BitConverter.GetBytes(ctrl));   // mov r11, ctrl
            b.AddRange(new byte[] { 0x49, 0xFF, 0x43, CB_HEARTBEAT });                        // inc qword [r11+CB_HEARTBEAT]
            b.AddRange(new byte[] { 0x49, 0x83, 0x7B, CB_PENDING, 0x01 });                    // cmp qword [r11+CB_PENDING], 1

            // The service branch. Built separately so the jne displacement is measured, not hand-counted.
            var svc = new List<byte>();
            svc.AddRange(new byte[] { 0x49, 0x8B, 0x4B, CB_A0 });    // mov rcx, [r11+CB_A0]
            svc.AddRange(new byte[] { 0x49, 0x8B, 0x53, CB_A1 });    // mov rdx, [r11+CB_A1]
            svc.AddRange(new byte[] { 0x4D, 0x8B, 0x43, CB_A2 });    // mov r8,  [r11+CB_A2]
            svc.AddRange(new byte[] { 0x4D, 0x8B, 0x4B, CB_A3 });    // mov r9,  [r11+CB_A3]
            svc.AddRange(new byte[] { 0x49, 0x8B, 0x03 });           // mov rax, [r11]  (fn)
            svc.AddRange(new byte[] { 0xFF, 0xD0 });                 // call rax   (clobbers r11 — it's volatile)
            svc.AddRange(new byte[] { 0x49, 0xBB }); svc.AddRange(BitConverter.GetBytes(ctrl)); // mov r11, ctrl  (reload!)
            svc.AddRange(new byte[] { 0x49, 0x89, 0x43, CB_RESULT }); // mov [r11+CB_RESULT], rax
            svc.AddRange(new byte[] { 0x49, 0xC7, 0x43, CB_PENDING, 0x02, 0x00, 0x00, 0x00 });  // mov qword [r11+CB_PENDING], 2

            b.AddRange(new byte[] { 0x75, (byte)svc.Count });      // jne done (skip the service branch)
            b.AddRange(svc);
            // done:
            b.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });     // add rsp, 0x28
            b.AddRange(new byte[] { 0x41, 0x5B });                 // pop r11
            b.AddRange(new byte[] { 0x41, 0x5A });                 // pop r10
            b.AddRange(new byte[] { 0x41, 0x59 });                 // pop r9
            b.AddRange(new byte[] { 0x41, 0x58 });                 // pop r8
            b.Add(0x5A);                                           // pop rdx
            b.Add(0x59);                                           // pop rcx
            b.Add(0x9D);                                           // popfq
            b.Add(0x58);                                           // pop rax
            b.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 }); // jmp qword ptr [rip+0]
            b.AddRange(BitConverter.GetBytes(trampoline));
            return b.ToArray();
        }

        private static byte[] BuildTrampoline(byte[] stolen, long resumeAddr)
        {
            var b = new List<byte>(stolen);
            b.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 }); // jmp qword ptr [rip+0]
            b.AddRange(BitConverter.GetBytes(resumeAddr));
            return b.ToArray();
        }

        private static byte[] BuildEntryPatch(long hookAddr, int totalLen)
        {
            var b = new List<byte>();
            b.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 }); // jmp qword ptr [rip+0]
            b.AddRange(BitConverter.GetBytes(hookAddr));
            while (b.Count < totalLen) b.Add(0x90);                        // nop pad to a whole-instruction boundary
            return b.ToArray();
        }

        // --- Helpers -------------------------------------------------------------------------------

        private ulong ReadPtr(long addr) => MemoryHelper.ReadU64(_hProc, (IntPtr)addr);

        private List<IntPtr> SuspendAllThreads()
        {
            var handles = new List<IntPtr>();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snapshot == INVALID_HANDLE_VALUE)
                throw new InvalidOperationException($"CreateToolhelp32Snapshot failed (error {Marshal.GetLastWin32Error()})");
            try
            {
                var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
                if (!Thread32First(snapshot, ref te)) return handles;
                do
                {
                    if (te.th32OwnerProcessID != (uint)_pid) continue;
                    IntPtr th = OpenThread(THREAD_SUSPEND_RESUME, false, te.th32ThreadID);
                    if (th == IntPtr.Zero) continue;
                    if (SuspendThread(th) == unchecked((uint)-1)) { MemoryHelper.CloseHandleSafe(th); continue; }
                    handles.Add(th);
                }
                while (Thread32Next(snapshot, ref te));
            }
            finally { MemoryHelper.CloseHandleSafe(snapshot); }
            return handles;
        }

        private static void ResumeThreads(List<IntPtr> handles)
        {
            foreach (IntPtr h in handles)
            {
                try { ResumeThread(h); } catch { /* best effort */ }
                MemoryHelper.CloseHandleSafe(h);
            }
        }

        // --- P/Invoke ------------------------------------------------------------------------------

        private const uint MEM_COMMIT_RESERVE = 0x1000 | 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint TH32CS_SNAPTHREAD = 0x00000004;
        private const uint THREAD_SUSPEND_RESUME = 0x0002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = (IntPtr)(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct THREADENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ThreadID;
            public uint th32OwnerProcessID;
            public int tpBasePri;
            public int tpDeltaPri;
            public uint dwFlags;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, int size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, int size, uint type);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 te);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 te);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SuspendThread(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr h);

        public ValueTask DisposeAsync()
        {
            if (_hProc != IntPtr.Zero)
            {
                UninstallHook();
                MemoryHelper.CloseHandleSafe(_hProc);
                _hProc = IntPtr.Zero;
            }
            return ValueTask.CompletedTask;
        }
    }
}
