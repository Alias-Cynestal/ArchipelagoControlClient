using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Ap.Control.Ui
{
    internal sealed record ConnectRequest(string Host, string Port, string Slot, string? Password)
    {
        internal Uri ToUri()
        {
            string text = Host.Trim();
            if (text.Length == 0) throw new ArgumentException("no server address given");

            if (!text.Contains("://", StringComparison.Ordinal)) text = "ws://" + text;

            string authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
            int slash = authority.IndexOf('/');
            if (slash >= 0) authority = authority[..slash];

            var builder = new UriBuilder(text);
            if (!authority.Contains(':'))
            {
                string port = Port.Trim();
                if (port.Length == 0) port = "38281";
                if (!int.TryParse(port, out int number) || number is < 1 or > 65535)
                    throw new ArgumentException($"'{Port}' is not a valid port");
                builder.Port = number;
            }
            return builder.Uri;
        }
    }

    internal sealed class UiBridge : IAsyncDisposable
    {
        // Deliberately NOT 38281: that is Archipelago's own default server port, so a player
        // hosting a room locally would have the game's UI connecting to the AP server instead of
        // to this client.
        internal const int DefaultPort = 38381;

        private readonly int _port;
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentDictionary<WebSocketPeer, string> _views = new();
        private TcpListener? _listener;
        private Task? _acceptLoop;

        /// <summary>The last status pushed, replayed to views that attach later.</summary>
        private UiStatus _status = UiStatus.Idle;

        internal event Func<ConnectRequest, Task>? ConnectRequested;
        internal event Func<Task>? StatusRequested;

        internal UiBridge(int port) => _port = port;

        internal void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);

            _listener.ExclusiveAddressUse = true;
            try
            {
                _listener.Start();
            }
            catch (SocketException e)
            {
                throw new InvalidOperationException(
                    $"cannot listen on 127.0.0.1:{_port} — is another Ap.Control client already "
                    + $"running? ({e.SocketErrorCode})", e);
            }

            Console.WriteLine($"UI bridge: listening on 127.0.0.1:{_port}");
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }

                _ = Task.Run(() => ServeAsync(client, token), CancellationToken.None);
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken token)
        {
            WebSocketPeer? peer = null;
            try
            {
                peer = await WebSocketPeer.AcceptAsync(client, token).ConfigureAwait(false);
                if (peer is null) return;

                await foreach (string message in peer.ReadAsync(token).ConfigureAwait(false))
                    await OnMessageAsync(peer, message).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[ui-bridge] {peer?.Peer ?? "?"}: {e.Message}");
            }
            finally
            {
                if (peer is not null)
                {
                    _views.TryRemove(peer, out _);
                    peer.Dispose();
                }
                client.Dispose();
            }
        }

        private async Task OnMessageAsync(WebSocketPeer peer, string message)
        {
            if (message.StartsWith("hello:", StringComparison.Ordinal))
            {
                string view = message[6..];
                if (view.Length == 0) view = "menu";
                _views[peer] = view;

                string? code = UiPayloadSource.Read(view);
                if (code is null)
                {
                    Console.Error.WriteLine($"[ui-bridge] no UI payload for view '{view}'");
                    return;
                }
                await peer.SendAsync(code).ConfigureAwait(false);
                // A view that attaches mid-session should not sit there saying "waiting for client".
                await peer.SendAsync(_status.ToJs()).ConfigureAwait(false);
                return;
            }

            if (message.StartsWith("action:connect ", StringComparison.Ordinal))
            {
                await OnConnectAsync(message["action:connect ".Length..]).ConfigureAwait(false);
                return;
            }

            if (message == "action:status")
            {
                if (StatusRequested is { } ask) await ask().ConfigureAwait(false);
                else await peer.SendAsync(_status.ToJs()).ConfigureAwait(false);
                return;
            }

            if (message.StartsWith("error:", StringComparison.Ordinal))
                Console.Error.WriteLine($"[ui-js] {message[6..]}");
        }

        private async Task OnConnectAsync(string json)
        {
            ConnectRequest request;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                string Field(string name) =>
                    root.TryGetProperty(name, out JsonElement v) ? v.GetString() ?? "" : "";

                string password = Field("password");
                request = new ConnectRequest(Field("host"), Field("port"), Field("slot"),
                    password.Length == 0 ? null : password);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[ui-bridge] unreadable connect request: {e.Message}");
                await PushAsync(UiStatus.Failed("bad request")).ConfigureAwait(false);
                return;
            }

            Console.WriteLine($"UI bridge: connect requested — {request.Host}:{request.Port} as '{request.Slot}'"
                + (request.Password is null ? "" : " (with password)"));

            if (ConnectRequested is { } handler) await handler(request).ConfigureAwait(false);
            else await PushAsync(UiStatus.Failed("client cannot connect")).ConfigureAwait(false);
        }

        internal async Task PushAsync(UiStatus status)
        {
            _status = status;
            await BroadcastAsync(status.ToJs()).ConfigureAwait(false);
        }

        internal async Task BroadcastAsync(string js)
        {
            foreach (WebSocketPeer peer in _views.Keys)
            {
                try
                {
                    await peer.SendAsync(js, _stopping.Token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[ui-bridge] push to {peer.Peer} failed: {e.Message}");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            try { _listener?.Stop(); } catch { /* already stopped */ }

            if (_acceptLoop is not null)
            {
                try { await _acceptLoop.ConfigureAwait(false); } catch { /* shutting down */ }
            }

            foreach (WebSocketPeer peer in _views.Keys) peer.Dispose();
            _views.Clear();
            _stopping.Dispose();
        }
    }
}
