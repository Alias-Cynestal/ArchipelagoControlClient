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

        /// <summary>
        /// Enforcement must not start until the server has replayed the item list.
        /// </summary>
        private const int InitialSyncGraceMs = 3000;
        private const int ItemQuietMs = 1500;

        /// <summary>
        /// How often to re-write the elevator UI bits.
        /// </summary>
        private const int UiBitIntervalMs = 100;

        private ArchipelagoSession _session;
        private readonly IItemGranter _granter;
        private readonly IGameFlowController _gameflow;
        private readonly IUiModelController _uiModel;
        private readonly ApItemMap _itemMap;
        private readonly ArchipelagoConnectionModel _model;

        // Archipelago-granted state, the source of truth reconciliation enforces against the live game.
        private readonly object _grantLock = new();
        private readonly HashSet<string> _grantedFlags = new();
        // Which elevator UI bits AP has granted. 
        private readonly HashSet<ElevatorBit> _grantedBits = new();
        private int _grantedClearance;
        // How many Progressive Clearance Level items have arrived. 
        private int _progressiveClearance;
        // Last enforcement target we logged, so steady-state reconciles stay quiet and only changes show.
        private string? _lastReconcileSig;
        // Serialises reconcile passes — the save-watch thread and the timer both drive GameFlow writes.
        private readonly object _reconcileLock = new();
        private Timer? _reconcileTimer;
        private Timer? _uiBitTimer;
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

        public ArchipelagoClient(ArchipelagoConnectionModel model, IItemGranter granter,
            IGameFlowController gameflow, IUiModelController uiModel, ApItemMap itemMap)
        {
            _session = createSession(model);
            _granter = granter;
            _gameflow = gameflow;
            _uiModel = uiModel;
            _itemMap = itemMap;
            _model = model;
        }

        public Task StartClient() 
        {
            LoginResult result = _session.TryConnectAndLogin(GameName, _model.Username, ItemsHandlingFlags.IncludeOwnItems, password: _model.Password);
            if(!result.Successful)
            {
                return Task.FromException(new Exception("Failed to connect to Archipelago server."));
            }

            _connectedUtc = DateTime.UtcNow;
            _reconcileTimer = new Timer(OnReconcileTick, null, ReconcileIntervalMs, Timeout.Infinite);
            _uiBitTimer = new Timer(OnUiBitTick, null, UiBitIntervalMs, Timeout.Infinite);
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

            foreach (var mission in change.Diff.MissionChanges)
            {
                if (mission.NewState == 2)
                {
                    Console.WriteLine($"Completing mission completed check for location {_session.Locations.GetLocationNameFromId((long)mission.GidMissionId)}");
                    _session.Locations.CompleteLocationChecks((long)mission.GidMissionId);
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
        /// Re-write the elevator UI bits to set which sectors are available to the player.
        /// </summary>
        private void OnUiBitTick(object? _)
        {
            try
            {
                if (!ReadyToEnforce()) return;
                ElevatorBit[] bits;
                lock (_grantLock) bits = _grantedBits.ToArray();
                _uiModel.SetBits(bits.ToHashSet());
            }
            catch
            {
                // Deliberately swallowed — this runs 10x/second and must never spam or die.
            }
            finally
            {
                Rearm(_uiBitTimer, UiBitIntervalMs);
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
            _uiBitTimer?.Dispose();
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

                    case ApActionKind.Flag:
                        lock (_grantLock)
                        {
                            foreach (var flag in action.Flags!) _grantedFlags.Add(flag);
                            if (action.Bits is not null)
                                foreach (var bit in action.Bits) _grantedBits.Add(bit);
                        }
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
        {
            var log = GrantLog();
            if (log.IsGranted(ordinal))
            {
                Console.WriteLine($"  -> inventory: GID 0x{gid:X} already granted on an earlier connect — skipped");
                return;
            }

            GrantResult gr = _granter.GiveItemAsync(gid).Result;
            if (gr.Ok)
                log.MarkGranted(ordinal);

            Console.WriteLine($"  -> inventory: GID 0x{gid:X} -> {gr}"
                + (gr.Ok && !gr.Accepted ? notAcceptedHint : "")
                + (gr.Ok ? "" : " (not recorded — will retry on next connect)"));
        }
    }
}
