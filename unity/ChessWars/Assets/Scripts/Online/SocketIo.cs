// A minimal Socket.IO (v4 server, Engine.IO 4) client over a plain WebSocket, for the War Room and live battles.
// It speaks just what the Chess Wars server uses: connect with an auth token, events both ways, and replies
// (acknowledgements) to the events we send. Network work runs on a background task; everything the game sees
// (events, replies, connect and disconnect) is handed over on the main thread by Pump(). Reconnects by itself.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChessWars.Online
{
    public sealed class SocketIo : IDisposable
    {
        readonly Uri uri;
        readonly string authJson;
        readonly ConcurrentQueue<Action> inbox = new ConcurrentQueue<Action>();
        readonly Dictionary<int, Action<Dictionary<string, object>>> acks = new Dictionary<int, Action<Dictionary<string, object>>>();
        readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource life = new CancellationTokenSource();
        ClientWebSocket ws;
        int nextAck;
        volatile bool connected;
        long lastHeard;   // ticks: no ping from the server for a minute means the line is dead

        public event Action Connected, Disconnected;
        /// <summary>The server refused the connection (e.g. "unauthorized"): no automatic retry after this.</summary>
        public event Action<string> Refused;
        public event Action<string, Dictionary<string, object>> Event;

        public bool IsConnected => connected;

        public SocketIo(string server, string token)
        {
            var b = new UriBuilder(server.TrimEnd('/') + "/socket.io/") { Query = "EIO=4&transport=websocket" };
            b.Scheme = b.Scheme == "https" ? "wss" : "ws";
            if (b.Port == 443 || b.Port == 80) b.Port = -1;
            uri = b.Uri;
            authJson = Json.Write(new Dictionary<string, object> { ["token"] = token });
            Task.Run(Run);
        }

        /// <summary>Delivers what arrived since last frame. Call from Update.</summary>
        public void Pump()
        {
            while (inbox.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
            }
            if (connected && DateTime.UtcNow.Ticks - Interlocked.Read(ref lastHeard) > TimeSpan.FromSeconds(60).Ticks)
            {
                Interlocked.Exchange(ref lastHeard, DateTime.UtcNow.Ticks);
                try { ws?.Abort(); } catch (Exception) { }
            }
        }

        /// <summary>Sends an event; `reply` gets the server's answer ({ ok, ... } or { error, message }) on the main
        /// thread, or { error: "timeout" } after 8 seconds, or { error: "offline" } when not connected.</summary>
        public void Emit(string name, Dictionary<string, object> payload, Action<Dictionary<string, object>> reply = null)
        {
            if (!connected)
            {
                reply?.Invoke(Error("offline", "Not connected to the server. Check your connection."));
                return;
            }
            int id;
            lock (acks)
            {
                id = ++nextAck;
                if (reply != null) acks[id] = reply;
            }
            string packet = "42" + id + Json.Write(new List<object> { name, payload ?? new Dictionary<string, object>() });
            _ = Send(packet);
            if (reply != null)
                _ = Task.Delay(8000).ContinueWith(_ =>
                {
                    Action<Dictionary<string, object>> cb = null;
                    lock (acks)
                        if (acks.TryGetValue(id, out cb)) acks.Remove(id);
                    if (cb != null) inbox.Enqueue(() => cb(Error("timeout", "The server didn't answer. Check your connection.")));
                });
        }

        public static Dictionary<string, object> Error(string code, string message) =>
            new Dictionary<string, object> { ["error"] = code, ["message"] = message };

        async Task Send(string text)
        {
            var socket = ws;
            if (socket == null) return;
            await sending.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, life.Token);
            }
            catch (Exception) { /* the receive loop notices and reconnects */ }
            finally { sending.Release(); }
        }

        async Task Run()
        {
            int delay = 1000;
            while (!life.IsCancellationRequested)
            {
                bool refused = false;
                try { refused = await Session(); }
                catch (Exception) { }
                bool was = connected;
                if (was)
                {
                    connected = false;
                    inbox.Enqueue(() => Disconnected?.Invoke());
                }
                FailPendingAcks();
                if (refused || life.IsCancellationRequested) return;
                // Back off while the server can't be reached; straight back after a working line drops.
                delay = was ? 1000 : Math.Min(delay * 2, 15000);
                try { await Task.Delay(delay, life.Token); } catch (Exception) { return; }
            }
        }

        void FailPendingAcks()
        {
            List<Action<Dictionary<string, object>>> pending;
            lock (acks)
            {
                pending = new List<Action<Dictionary<string, object>>>(acks.Values);
                acks.Clear();
            }
            foreach (var cb in pending) inbox.Enqueue(() => cb(Error("offline", "The connection dropped. Try again.")));
        }

        // One connection, until it drops. True when the server refused us (bad token): don't retry.
        async Task<bool> Session()
        {
            using var socket = new ClientWebSocket();
            ws = socket;
            Interlocked.Exchange(ref lastHeard, DateTime.UtcNow.Ticks);
            await socket.ConnectAsync(uri, life.Token);
            var buffer = new byte[16 * 1024];
            var message = new StringBuilder();
            while (socket.State == WebSocketState.Open && !life.IsCancellationRequested)
            {
                message.Clear();
                WebSocketReceiveResult r;
                do
                {
                    r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), life.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return false;
                    message.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                } while (!r.EndOfMessage);
                Interlocked.Exchange(ref lastHeard, DateTime.UtcNow.Ticks);
                if (Handle(message.ToString())) return true;
            }
            return false;
        }

        // Engine.IO: 0 open, 2 ping, 3 pong, 4 message. Socket.IO inside a message: 0 connect, 1 disconnect,
        // 2 event, 3 ack, 4 connect error. Returns true when the server refused the connection.
        bool Handle(string p)
        {
            if (p.Length == 0) return false;
            switch (p[0])
            {
                case '0': _ = Send("40" + authJson); return false;   // join the default namespace with our token
                case '2': _ = Send("3"); return false;
                case '1': try { ws.Abort(); } catch (Exception) { } return false;
                case '4': break;
                default: return false;
            }
            if (p.Length < 2) return false;
            char type = p[1];
            string rest = p.Substring(2);
            switch (type)
            {
                case '0':
                    connected = true;
                    inbox.Enqueue(() => Connected?.Invoke());
                    return false;
                case '1':
                    try { ws.Abort(); } catch (Exception) { }
                    return false;
                case '4':
                {
                    string reason = "refused";
                    try { reason = (Json.Parse(rest) as Dictionary<string, object>).Str("message", reason); } catch (Exception) { }
                    inbox.Enqueue(() => Refused?.Invoke(reason));
                    return true;
                }
                case '2':
                case '3':
                {
                    int i = 0, id = -1;
                    while (i < rest.Length && char.IsDigit(rest[i])) i++;
                    if (i > 0) id = int.Parse(rest.Substring(0, i));
                    List<object> args;
                    try { args = Json.Parse(rest.Substring(i)) as List<object>; }
                    catch (Exception) { return false; }
                    if (args == null) return false;
                    if (type == '2')
                    {
                        if (args.Count == 0 || !(args[0] is string name)) return false;
                        var data = args.Count > 1 ? args[1] as Dictionary<string, object> : null;
                        inbox.Enqueue(() => Event?.Invoke(name, data));
                    }
                    else
                    {
                        Action<Dictionary<string, object>> cb = null;
                        lock (acks)
                            if (acks.TryGetValue(id, out cb)) acks.Remove(id);
                        var data = args.Count > 0 ? args[0] as Dictionary<string, object> : null;
                        if (cb != null) inbox.Enqueue(() => cb(data ?? new Dictionary<string, object>()));
                    }
                    return false;
                }
            }
            return false;
        }

        public void Dispose()
        {
            life.Cancel();
            try { ws?.Abort(); } catch (Exception) { }
            connected = false;
        }
    }
}
