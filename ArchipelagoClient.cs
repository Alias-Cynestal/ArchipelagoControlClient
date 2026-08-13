using Ap.Control.Models;
using Ap.Control.Utils;
using Ap.Control.Utils.Interfaces;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;

namespace Ap.Control
{
    internal class ArchipelagoClient : ISaveChangeNotifier, IDisposable
    {
        private static readonly String GameName = "Control";

        /// <summary>
        /// How often to re-assert AP's state over the live game.
        /// </summary>
        private const int ReconcileIntervalMs = 1000;

        private const long TakeControlGid = 371954468016324688; // GID for the "Take Control" item, which is a prerequisite for any other inventory grant.

        /// <summary>
        /// Enforcement must not start until the server has replayed the item list.
        /// </summary>
        private const int InitialSyncGraceMs = 3000;
        private const int ItemQuietMs = 1500;

        private const int MaxMilestoneLevel = 3;

        private ArchipelagoSession _session;
        private readonly IItemGranter _granter;
        private readonly IAbilityGranter _abilityGranter;
        private readonly IGameFlowController _gameflow;
        private readonly ApItemMap _itemMap;
        private readonly ArchipelagoConnectionModel _model;

        // Archipelago-granted state, the source of truth reconciliation enforces against the live game.
        private readonly object _grantLock = new();
        private readonly HashSet<string> _grantedFlags = new();
        // Which elevator sectors AP has granted, as reported to the in-game page.
        private readonly HashSet<ElevatorBit> _grantedBits = new();
        private int _grantedClearance;
        // How many Progressive Clearance Level items have arrived.
        private int _progressiveClearance;
        // How many Progressive Ability Milestone items have arrived (Nth -> milestone level N).
        private int _progressiveMilestone;
        // Last enforcement target we logged, so steady-state reconciles stay quiet and only changes show.
        private string? _lastReconcileSig;
        // Serialises reconcile passes — the save-watch thread and the timer both drive GameFlow writes.
        private readonly object _reconcileLock = new();
        private Timer? _reconcileTimer;
        // Set in Dispose so an in-flight pass does not re-arm a timer that is going away.
        private volatile bool _disposed;
        // Initial-sync gate: enforcement stays off until the replayed item batch has settled.
        private DateTime _connectedUtc = DateTime.MaxValue;
        private DateTime _lastItemUtc = DateTime.MinValue;
        private bool _enforcing;

        // 1-based position of each item in the received-item stream.
        private int _itemOrdinal;
        // Which inventory grants have physically happened.
        private InventoryGrantLog? _grantLog;

        // One-shot grants the game could not take yet — almost always because the player is still in
        // the main menu when the server replays the item list. Retried in order on the reconcile tick.
        private readonly List<DeferredGrant> _deferredGrants = new();
        private bool _deferralAnnounced;

        public ArchipelagoClient(ArchipelagoConnectionModel model, IItemGranter granter, IAbilityGranter abilityGranter,
            IGameFlowController gameflow, ApItemMap itemMap)
        {
            _session = createSession(model);
            _granter = granter;
            _abilityGranter = abilityGranter;
            _gameflow = gameflow;
            _itemMap = itemMap;
            _model = model;
        }

        /// <summary>
        /// Whether the session is live. Read by the UI bridge, which can be asked for status at any
        /// time — including before a connection has ever been made.
        /// </summary>
        public bool IsConnected => _session.Socket.Connected;

        public string SlotName => _model.Username;

        public string Seed
        {
            get
            {
                try { return _session.RoomState.Seed ?? "-"; }
                catch { return "-"; }
            }
        }

        public int LocationsChecked
        {
            get
            {
                try { return _session.Locations.AllLocationsChecked.Count; }
                catch { return 0; }
            }
        }

        public int LocationsTotal
        {
            get
            {
                try { return _session.Locations.AllLocations.Count; }
                catch { return 0; }
            }
        }

        public Task StartClient()
        {
            LoginResult result = _session.TryConnectAndLogin(GameName, _model.Username, ItemsHandlingFlags.IncludeOwnItems, password: _model.Password);
            if(!result.Successful)
            {
                string reason = result is LoginFailure failure && failure.Errors.Length > 0
                    ? string.Join("; ", failure.Errors)
                    : "Failed to connect to Archipelago server.";
                return Task.FromException(new Exception(reason));
            }

            _connectedUtc = DateTime.UtcNow;
            _reconcileTimer = new Timer(OnReconcileTick, null, ReconcileIntervalMs, Timeout.Infinite);
            return Task.CompletedTask;
        }

        public Task NotifyAsync(SaveChangedEventArgs change, CancellationToken cancellationToken = default)
        {
            foreach (var location in change.Diff.NewFoundLocations)
            {
                Console.WriteLine($"Completing location check for location {_session.Locations.GetLocationNameFromId((long)location)}");
                _session.Locations.CompleteLocationChecks((long)location);
            }

            foreach (var sector in change.Diff.NewSectorsVisited)
            {
                Console.WriteLine($"Completing sector check for location {_session.Locations.GetLocationNameFromId((long)sector)}");
                _session.Locations.CompleteLocationChecks((long)sector);
            }

            foreach (var controlPoint in change.Diff.NewUnlockedControlPoints)
            {
                Console.WriteLine($"Completing control point check for location {_session.Locations.GetLocationNameFromId((long)controlPoint)}");
                _session.Locations.CompleteLocationChecks((long)controlPoint);
            }

            foreach (var collectible in change.Diff.NewCollectibles)
            {
                Console.WriteLine($"Completing collectible check for location {_session.Locations.GetLocationNameFromId((long)collectible)}");
                _session.Locations.CompleteLocationChecks((long)collectible);
            }

            foreach (var mission in change.Diff.MissionChanges)
            {
                if (mission.NewState == 2)
                {
                    Console.WriteLine($"Completing mission completed check for location {_session.Locations.GetLocationNameFromId((long)mission.GidMissionId)}");
                    _session.Locations.CompleteLocationChecks((long)mission.GidMissionId);
                    if (mission.GidMissionId == TakeControlGid)
                    {
                        _session.SetGoalAchieved();
                    }
                }
            }

            ReconcileLocks();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Relock every mapped sector flag AP hasn't granted, and re-apply every flag (and the clearance
        /// level) it has.
        /// </summary>
        private void ReconcileLocks()
        {
            lock (_reconcileLock)
            {
                string[] ungranted, granted;
                int keepClearance;
                lock (_grantLock)
                {
                    ungranted = _itemMap.FlagNames.Where(f => !_grantedFlags.Contains(f)).ToArray();
                    granted = _grantedFlags.ToArray();
                    keepClearance = _grantedClearance;
                }

                // One sweep for the whole target state: ungranted flags off, granted flags on, and the
                // keycards that derive clearance set to exactly the granted level.
                var desired = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var f in ungranted) desired[f] = false;
                foreach (var f in granted) desired[f] = true;
                for (int i = 1; i <= ApClearanceIds.MaxLevel; i++) desired[$"KEY{i}"] = i <= keepClearance;

                int nodes = _gameflow.ApplyFlags(desired);

                // Enforcement writes every tick; only announce it when the target actually shifts.
                string sig = $"{keepClearance}|{string.Join(',', ungranted.OrderBy(f => f))}"
                           + $"|{string.Join(',', granted.OrderBy(f => f))}";
                if (sig != _lastReconcileSig)
                {
                    _lastReconcileSig = sig;
                    Console.WriteLine($"  -> reconcile: {ungranted.Length} ungranted flag(s) relocked, "
                        + $"{granted.Length} granted flag(s) re-applied, clearance held at {keepClearance} "
                        + $"({nodes} node(s) written)");
                }
            }
        }

        /// <summary>
        /// True once the connect grace has passed AND no item has arrived recently.
        /// </summary>
        private bool ReadyToEnforce()
        {
            DateTime lastItem;
            lock (_grantLock) lastItem = _lastItemUtc;

            var now = DateTime.UtcNow;
            if (now - _connectedUtc < TimeSpan.FromMilliseconds(InitialSyncGraceMs)) return false;
            if (lastItem != DateTime.MinValue
                && now - lastItem < TimeSpan.FromMilliseconds(ItemQuietMs)) return false;

            if (!_enforcing)
            {
                _enforcing = true;
                Console.WriteLine($"Enforcement active: reconciling every {ReconcileIntervalMs} ms "
                    + "(the game re-asserts sector flags on its own; AP takes them back).");
            }
            return true;
        }

        /// <summary>
        /// Periodic reconcile. Failures are swallowed deliberately — the game not running, or a map
        /// rebuild mid-scan, must not kill the timer; the next tick simply tries again.
        /// </summary>
        private void OnReconcileTick(object? _)
        {
            try
            {
                FlushDeferredGrants();

                if (!ReadyToEnforce()) return;
                ReconcileLocks();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[reconcile] {e.Message}");
            }
            finally
            {
                Rearm(_reconcileTimer, ReconcileIntervalMs);
            }
        }

        /// <summary>
        /// Schedule the next (single) tick of a one-shot timer. 
        /// </summary>
        private void Rearm(Timer? timer, int dueMs)
        {
            if (_disposed) return;
            try { timer?.Change(dueMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* shutting down */ }
        }

        public void Dispose()
        {
            _disposed = true;
            _reconcileTimer?.Dispose();
        }

        private ArchipelagoSession createSession(ArchipelagoConnectionModel model)
        {
            ArchipelagoSession session = ArchipelagoSessionFactory.CreateSession(model.Uri);
            session.Items.ItemReceived += (helper) =>
            {
                ItemInfo itemInfo = helper.PeekItem();

                int ordinal;
                lock (_grantLock)
                {
                    ordinal = ++_itemOrdinal;
                    _lastItemUtc = DateTime.UtcNow;   // holds enforcement off while the batch replays
                }

                Console.WriteLine($"Received item #{ordinal} {itemInfo.ItemName} (id {itemInfo.ItemId} / 0x{unchecked((ulong)itemInfo.ItemId):X}) "
                    + $"from player {itemInfo.Player} by completing {itemInfo.LocationName}");
                ApplyItem(itemInfo, ordinal);

                helper.DequeueItem();
            };
            return session;
        }

        /// <summary>
        /// The grant log for the connected seed/slot.
        /// </summary>
        private InventoryGrantLog GrantLog()
        {
            lock (_grantLock)
            {
                if (_grantLog is not null)
                    return _grantLog;

                string seed = _session.RoomState.Seed ?? "unknown";
                int slot = _session.ConnectionInfo.Slot;
                try
                {
                    _grantLog = InventoryGrantLog.Load(
                        Path.Combine(AppContext.BaseDirectory, "state"), seed, slot);
                    Console.WriteLine($"Inventory grant log: {_grantLog.Count} prior grant(s) for seed {seed} slot {slot} "
                        + $"({_grantLog.Path})");
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[grant-log] unusable, continuing in memory: {e.Message}");
                    _grantLog = InventoryGrantLog.InMemory(seed, slot);
                }
                return _grantLog;
            }
        }

        // Route a received Archipelago item to the correct in-game lever, per the item map.
        private void ApplyItem(ItemInfo item, int ordinal)
        {
            if (_itemMap.TryResolve(item.ItemId, out var action))
            {
                switch (action.Kind)
                {
                    case ApActionKind.Inventory:
                        GrantInventory(action.Gid, ordinal,
                            " (game returned 0 — not a spawnable def, or content not resident here)");
                        break;

                    case ApActionKind.Ability:
                        GrantAbility(action.Gid, ordinal);
                        break;

                    case ApActionKind.ProgressiveMilestone:
                        GrantProgressiveMilestone(ordinal);
                        break;

                    case ApActionKind.Flag:
                        lock (_grantLock)
                        {
                            foreach (var flag in action.Flags!) _grantedFlags.Add(flag);
                            if (action.Bits is not null)
                                foreach (var bit in action.Bits) _grantedBits.Add(bit);
                        }
                        // Elevator access is part of what the page shows now, so a granted sector
                        // should reach it without waiting for the next status request.
                        NotifyStateChanged();

                        foreach (var flag in action.Flags!)
                        {
                            int flagNodes = _gameflow.SetFlag(flag, true);
                            Console.WriteLine($"  -> gameflow: {flag} = true"
                                + (flagNodes == 0 ? " (0 nodes — is the game running?)" : $" ({flagNodes} node(s))"));
                        }
                        if (action.Bits is not null)
                            Console.WriteLine($"  -> elevator bits: +{string.Join(", ", action.Bits)}");
                        break;

                    case ApActionKind.Clearance:
                        lock (_grantLock) _grantedClearance = Math.Max(_grantedClearance, action.Level);
                        int clearanceNodes = _gameflow.SetClearance(action.Level);
                        Console.WriteLine($"  -> clearance: level {action.Level} (KEY1..{action.Level})"
                            + (clearanceNodes == 0 ? " (0 nodes — is the game running?)" : $" ({clearanceNodes} node(s))"));
                        break;

                    case ApActionKind.ProgressiveClearance:
                        int level, count;
                        lock (_grantLock)
                        {
                            _progressiveClearance = Math.Min(_progressiveClearance + 1, ApClearanceIds.MaxLevel);
                            _grantedClearance = Math.Max(_grantedClearance, _progressiveClearance);
                            level = _grantedClearance;
                            count = _progressiveClearance;
                        }
                        int progressiveClearanceNodes = _gameflow.SetClearance(level);
                        Console.WriteLine($"  -> clearance: progressive #{count} -> level {level} (KEY1..{level})"
                            + (progressiveClearanceNodes == 0 ? " (0 nodes — is the game running?)" : $" ({progressiveClearanceNodes} node(s))"));
                        break;
                }
            }
            else
            {
                // No mapping: fall back to treating the id's raw 64-bit pattern as an inventory GID.
                ulong gid = unchecked((ulong)item.ItemId);
                Console.WriteLine($"  -> [warn] no item-map entry for id {item.ItemId} (0x{gid:X}); treating as inventory GID");
                GrantInventory(gid, ordinal,
                    " (game returned 0 — id is likely not a real GID; map it explicitly)");
            }
        }

        /// <summary>
        /// Spawn one inventory item into the game, unless the grant log says this ordinal was already granted on an earlier connect.
        /// </summary>
        private void GrantInventory(ulong gid, int ordinal, string notAcceptedHint)
            => GrantOnce(ordinal, $"inventory: GID 0x{gid:X}",
                () => _granter.GiveItemAsync(gid).Result, notAcceptedHint);

        private void GrantOnce(int ordinal, string itemName, Func<GrantResult> attempt, string notAcceptedHint = "")
        {
            var log = GrantLog();
            if (log.IsGranted(ordinal))
            {
                Console.WriteLine($"  -> {itemName} already granted on an earlier connect — skipped");
                return;
            }

            bool queued = false;
            lock (_grantLock)
            {
                if (_deferredGrants.Count > 0)
                {
                    _deferredGrants.Add(new DeferredGrant(ordinal, itemName, attempt, notAcceptedHint));
                    Console.WriteLine($"  -> {itemName} queued behind {_deferredGrants.Count - 1} earlier item(s)");
                    queued = true;
                }
            }

            if (queued)
            {
                NotifyStateChanged();
                return;
            }

            GrantResult gr = attempt();
            if (gr.Ok)
            {
                log.MarkGranted(ordinal);
                Console.WriteLine($"  -> {itemName} -> {gr}" + (gr.Accepted ? "" : notAcceptedHint));
                return;
            }

            lock (_grantLock) _deferredGrants.Add(new DeferredGrant(ordinal, itemName, attempt, notAcceptedHint));
            Console.WriteLine($"  -> {itemName} -> {gr}; held until the game can take it "
                + "(load a save — it will be granted then)");
            NotifyStateChanged();
        }

        private void FlushDeferredGrants()
        {
            DeferredGrant[] batch;
            lock (_grantLock)
            {
                if (_deferredGrants.Count == 0)
                {
                    _deferralAnnounced = false;
                    return;
                }
                batch = _deferredGrants.ToArray();
            }

            var stillWaiting = new List<DeferredGrant>();
            DeferredGrant? firstFailed = null;
            GrantResult? firstFailure = null;
            bool anyGranted = false;

            foreach (DeferredGrant item in batch)
            {
                GrantResult gr = item.Attempt();
                if (!gr.Ok)
                {
                    stillWaiting.Add(item);
                    firstFailed ??= item;
                    firstFailure ??= gr;
                    continue;
                }

                GrantLog().MarkGranted(item.Ordinal);
                anyGranted = true;
                Console.WriteLine($"  -> {item.What} -> {gr} (held item, now granted)"
                    + (gr.Accepted ? "" : item.NotAcceptedHint));
            }

            int waiting;
            bool announce;
            lock (_grantLock)
            {
                _deferredGrants.RemoveRange(0, batch.Length);
                _deferredGrants.InsertRange(0, stillWaiting);
                waiting = _deferredGrants.Count;

                if (anyGranted) _deferralAnnounced = false;
                announce = waiting > 0 && !_deferralAnnounced;
                if (announce) _deferralAnnounced = true;
                if (waiting == 0) _deferralAnnounced = false;
            }

            if (announce && firstFailed is not null)
                Console.WriteLine($"[items] {waiting} item(s) waiting for the game "
                    + $"(first: {firstFailed.What} — {firstFailure})");

            if (anyGranted) NotifyStateChanged();
        }

        public IReadOnlyDictionary<int, bool> ElevatorSectors
        {
            get
            {
                ElevatorBit[] granted;
                lock (_grantLock) granted = _grantedBits.ToArray();

                var map = new Dictionary<int, bool>
                {
                    [0] = true,
                    [1] = granted.Contains(ElevatorBit.Research),
                    [2] = granted.Contains(ElevatorBit.MaintenanceLobby),
                    [3] = granted.Contains(ElevatorBit.MaintenancePumpRoom),
                    [4] = granted.Contains(ElevatorBit.Containment),
                    [5] = granted.Contains(ElevatorBit.Investigation),
                };
                return map;
            }
        }

        public int PendingGrants
        {
            get { lock (_grantLock) return _deferredGrants.Count; }
        }

        public event Action? StateChanged;

        private void NotifyStateChanged()
        {
            try { StateChanged?.Invoke(); }
            catch (Exception e) { Console.Error.WriteLine($"[state] {e.Message}"); }
        }

        private sealed record DeferredGrant(int Ordinal, string What, Func<GrantResult> Attempt, string NotAcceptedHint);

        private void GrantAbility(ulong definitionGid, int ordinal)
            => GrantOnce(ordinal, $"ability: GID 0x{definitionGid:X}",
                () => _abilityGranter.GrantAbilityAsync(definitionGid).Result);


        /// <summary>
        /// Grant the ability-point milestone reward for the Nth Progressive Ability Milestone item received
        /// </summary>
        private void GrantProgressiveMilestone(int ordinal)
        {
            int level;
            lock (_grantLock)
            {
                _progressiveMilestone = Math.Min(_progressiveMilestone + 1, MaxMilestoneLevel);
                level = _progressiveMilestone;
            }

            GrantOnce(ordinal, $"milestone: progressive #{level} (weapon/mod slots up to level {level})",
                () => _abilityGranter.GrantMilestoneAsync(level).Result);
        }
    }
}
