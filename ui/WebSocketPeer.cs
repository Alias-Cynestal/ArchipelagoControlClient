using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Ap.Control.Ui
{
    internal sealed class WebSocketPeer : IDisposable
    {
        private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public string Peer { get; }

        private WebSocketPeer(TcpClient client, string peer)
        {
            _client = client;
            _stream = client.GetStream();
            Peer = peer;
        }

        internal static async Task<WebSocketPeer?> AcceptAsync(TcpClient client, CancellationToken token)
        {
            string peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
            var stream = client.GetStream();

            string head = await ReadHeadAsync(stream, token).ConfigureAwait(false);
            string? key = HeaderValue(head, "sec-websocket-key");
            bool wantsUpgrade = HeaderValue(head, "upgrade")?.Contains("websocket", StringComparison.OrdinalIgnoreCase) == true;

            if (!wantsUpgrade || key is null)
            {
                byte[] body = "Ap.Control UI bridge - WebSocket only"u8.ToArray();
                byte[] response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 400 Bad Request\r\nContent-Type: text/plain\r\n"
                    + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, token).ConfigureAwait(false);
                await stream.WriteAsync(body, token).ConfigureAwait(false);
                client.Dispose();
                return null;
            }

            string accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));
            byte[] upgrade = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: websocket\r\nConnection: Upgrade\r\n"
                + $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
            await stream.WriteAsync(upgrade, token).ConfigureAwait(false);

            return new WebSocketPeer(client, peer);
        }

        private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[8192];
            int filled = 0;
            while (filled < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                filled += read;
                if (Encoding.ASCII.GetString(buffer, 0, filled).Contains("\r\n\r\n")) break;
            }
            return Encoding.ASCII.GetString(buffer, 0, filled);
        }

        private static string? HeaderValue(string head, string name)
        {
            foreach (string line in head.Split("\r\n"))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line[(colon + 1)..].Trim();
            }
            return null;
        }

        internal async IAsyncEnumerable<string> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            while (!token.IsCancellationRequested)
            {
                byte[]? header = await ExactlyAsync(2, token).ConfigureAwait(false);
                if (header is null) yield break;

                int opcode = header[0] & 0x0F;
                bool masked = (header[1] & 0x80) != 0;
                long length = header[1] & 0x7F;

                if (length == 126)
                {
                    byte[]? ext = await ExactlyAsync(2, token).ConfigureAwait(false);
                    if (ext is null) yield break;
                    length = (ext[0] << 8) | ext[1];
                }
                else if (length == 127)
                {
                    byte[]? ext = await ExactlyAsync(8, token).ConfigureAwait(false);
                    if (ext is null) yield break;
                    length = 0;
                    foreach (byte b in ext) length = (length << 8) | b;
                }

                // A frame this large is not something the view sends; refusing beats allocating it.
                if (length > 16 * 1024 * 1024) yield break;

                byte[] mask = Array.Empty<byte>();
                if (masked)
                {
                    byte[]? m = await ExactlyAsync(4, token).ConfigureAwait(false);
                    if (m is null) yield break;
                    mask = m;
                }

                byte[] payload = Array.Empty<byte>();
                if (length > 0)
                {
                    byte[]? p = await ExactlyAsync((int)length, token).ConfigureAwait(false);
                    if (p is null) yield break;
                    payload = p;
                }

                if (masked)
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                if (opcode == 0x8) yield break;                       // close
                if (opcode == 0x1) yield return Encoding.UTF8.GetString(payload);
                // 0x9/0xA ping/pong and 0x2 binary are not part of this protocol; ignore them.
            }
        }

        private async Task<byte[]?> ExactlyAsync(int count, CancellationToken token)
        {
            var buffer = new byte[count];
            int filled = 0;
            while (filled < count)
            {
                int read;
                try
                {
                    read = await _stream.ReadAsync(buffer.AsMemory(filled, count - filled), token)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    return null;
                }
                if (read == 0) return null;
                filled += read;
            }
            return buffer;
        }

        internal async Task SendAsync(string text, CancellationToken token = default)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] header;
            if (body.Length < 126)
            {
                header = [0x81, (byte)body.Length];
            }
            else if (body.Length < 65536)
            {
                header = [0x81, 126, (byte)(body.Length >> 8), (byte)body.Length];
            }
            else
            {
                long n = body.Length;
                header = [0x81, 127,
                    (byte)(n >> 56), (byte)(n >> 48), (byte)(n >> 40), (byte)(n >> 32),
                    (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n];
            }

            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(header, token).ConfigureAwait(false);
                await _stream.WriteAsync(body, token).ConfigureAwait(false);
                await _stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _sendLock.Dispose();
            try { _client.Dispose(); } catch { /* already gone */ }
        }
    }
}
