using Ap.Control.Models;
using Ap.Control.Utils;
using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Ui
{
    internal sealed class ApSessionHost : IDisposable
    {
        private readonly IItemGranter _granter;
        private readonly IAbilityGranter _abilityGranter;
        private readonly IGameFlowController _gameflow;
        private readonly ApItemMap _itemMap;
        private readonly SaveNotifierRelay _relay;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private ArchipelagoClient? _client;

        private const int PushDebounceMs = 400;
        private Timer? _pushDebounce;

        internal ApSessionHost(IItemGranter granter, IAbilityGranter abilityGranter,
            IGameFlowController gameflow, ApItemMap itemMap, SaveNotifierRelay relay)
        {
            _granter = granter;
            _abilityGranter = abilityGranter;
            _gameflow = gameflow;
            _itemMap = itemMap;
            _relay = relay;
        }

        internal event Func<UiStatus, Task>? StatusChanged;

        internal UiStatus Status { get; private set; } = UiStatus.Idle;

        private async Task SetStatusAsync(UiStatus status)
        {
            Status = status;
            if (StatusChanged is { } handler) await handler(status).ConfigureAwait(false);
        }

        internal async Task RefreshAsync()
        {
            ArchipelagoClient? client = _client;
            if (client is null || !client.IsConnected)
            {
                await SetStatusAsync(Status with { }).ConfigureAwait(false);
                return;
            }
            await SetStatusAsync(Snapshot(client)).ConfigureAwait(false);
        }

        private Uri? _connectedTo;

        private UiStatus Snapshot(ArchipelagoClient client) => new(
            Status: "Connected",
            Slot: client.SlotName,
            Seed: client.Seed,
            ChecksFound: client.LocationsChecked,
            ChecksTotal: client.LocationsTotal,
            Pending: client.PendingGrants,
            Host: _connectedTo?.Host,
            Port: _connectedTo?.Port.ToString(),
            Elevator: client.ElevatorSectors);

        internal async Task ConnectAsync(ConnectRequest request)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Uri uri;
                try
                {
                    uri = request.ToUri();
                }
                catch (Exception e)
                {
                    await SetStatusAsync(UiStatus.Failed(e.Message)).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.Slot))
                {
                    await SetStatusAsync(UiStatus.Failed("no slot name")).ConfigureAwait(false);
                    return;
                }

                await SetStatusAsync(UiStatus.Connecting(uri.Host)).ConfigureAwait(false);

                _relay.Target = null;
                _client?.Dispose();
                _client = null;
                _connectedTo = null;

                var model = new ArchipelagoConnectionModel(uri, request.Slot, request.Password);
                var client = new ArchipelagoClient(model, _granter, _abilityGranter, _gameflow, _itemMap);
                client.StateChanged += SchedulePush;
                try
                {
                    await Task.Run(client.StartClient).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    client.Dispose();
                    string reason = e.InnerException?.Message ?? e.Message;
                    Console.Error.WriteLine($"[session] connect failed: {reason}");
                    await SetStatusAsync(UiStatus.Failed(reason)).ConfigureAwait(false);
                    return;
                }

                _client = client;
                _connectedTo = uri;
                _relay.Target = client;
                Console.WriteLine($"Connected to Archipelago at {uri} as '{request.Slot}'.");
                await SetStatusAsync(Snapshot(client)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void SchedulePush()
        {
            Timer? existing = Interlocked.Exchange(ref _pushDebounce, null);
            existing?.Dispose();

            var timer = new Timer(_ => _ = RefreshAsync(), null, PushDebounceMs, Timeout.Infinite);
            Timer? replaced = Interlocked.Exchange(ref _pushDebounce, timer);
            replaced?.Dispose();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _pushDebounce, null)?.Dispose();
            _relay.Target = null;
            _client?.Dispose();
            _gate.Dispose();
        }
    }

    internal sealed class SaveNotifierRelay : ISaveChangeNotifier
    {
        internal volatile ISaveChangeNotifier? Target;

        public Task NotifyAsync(SaveChangedEventArgs change, CancellationToken cancellationToken = default)
            => Target?.NotifyAsync(change, cancellationToken) ?? Task.CompletedTask;
    }
}
