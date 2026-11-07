using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace SinkLite.Core.Bas2
{
    // All session decisions are serialized by gate. Socket reads never own application state.
    internal sealed class ControlServer : IAsyncDisposable
    {
        private sealed class Peer
        {
            public ReceiverSession Session;
            public TcpClient Client;
            public IPAddress Local;
            public IPAddress Remote;
            public IPEndPoint MediaEndpoint;
            public TcpClient MediaClient;
        }
        private readonly object gate = new();
        private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);
        private readonly Dictionary<IPAddress, Socket> mediaSockets = new();
        private readonly HashSet<Task> workers = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly TcpListener controlListener;
        private readonly TcpListener mediaListener;
        private readonly string identity, name;
        private Peer active;
        private bool started;
        public int ControlPort => ((IPEndPoint)controlListener.LocalEndpoint).Port;
        public int MediaPort => ((IPEndPoint)mediaListener.LocalEndpoint).Port;
        public event Action<MediaFrame> AudioReceived;

        public ControlServer(IPAddress listenAddress, int controlPort, int mediaPort, string identity, string name)
        {
            controlListener = new TcpListener(listenAddress, controlPort);
            mediaListener = new TcpListener(listenAddress, mediaPort);
            this.identity = identity;
            this.name = name;
        }

        public void Start()
        {
            lock (gate)
            {
                if (started || cancellation.IsCancellationRequested) throw new InvalidOperationException("BAS2 server cannot restart");
                try { mediaListener.Start(8); controlListener.Start(8); }
                catch { mediaListener.Stop(); controlListener.Stop(); throw; }
                started = true;
                Track(AcceptLoop(false));
                Track(AcceptLoop(true));
                Track(ExpireLoop());
            }
        }

        private void Track(Task task)
        {
            lock (gate) workers.Add(task);
            _ = task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                lock (gate) workers.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task AcceptLoop(bool media)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await (media ? mediaListener : controlListener).AcceptTcpClientAsync(cancellation.Token);
                    client.NoDelay = true;
                    lock (gate)
                    {
                        if (workers.Count >= 40) { client.Dispose(); continue; }
                        Track(media ? ServeMedia(client) : ServeControl(client));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
        }

        private Socket GetMediaSocket(IPAddress address)
        {
            if (mediaSockets.TryGetValue(address, out var socket)) return socket;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { socket.Bind(new IPEndPoint(address, MediaPort)); }
            catch { socket.Dispose(); throw; }
            mediaSockets.Add(address, socket);
            Track(ReceiveMedia(socket, address));
            return socket;
        }

        private async Task ServeControl(TcpClient client)
        {
            Peer peer = null;
            try
            {
                var stream = client.GetStream();
                using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                var hello = ControlMessage.Parse(await WireCodec.ReadControlAsync(stream, helloTimeout.Token));
                lock (gate)
                {
                    if (peers.Count >= 16 || hello.Type != "hello") throw new InvalidDataException("Invalid BAS2 hello");
                    var local = ((IPEndPoint)client.Client.LocalEndPoint).Address;
                    GetMediaSocket(local);
                    var session = new ReceiverSession(identity, name, MediaPort, Environment.TickCount64,
                        () => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1);
                    peer = new Peer { Session = session, Client = client, Local = local, Remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address };
                    peers.Add(session.IdText, peer);
                }
                var request = hello;
                while (!cancellation.IsCancellationRequested)
                {
                    byte[] reply;
                    lock (gate)
                    {
                        reply = peer.Session.Handle(request, Environment.TickCount64, active == null || active == peer);
                        if (peer.Session.Phase == ConnectionPhase.Streaming) active = peer;
                        else if (active == peer) active = null;
                    }
                    await stream.WriteAsync(reply, cancellation.Token);
                    request = ControlMessage.Parse(await WireCodec.ReadControlAsync(stream, cancellation.Token));
                }
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is OperationCanceledException ||
                error is System.Text.Json.JsonException || error is InvalidOperationException || error is ObjectDisposedException) { }
            finally
            {
                lock (gate)
                {
                    if (peer != null)
                    {
                        peers.Remove(peer.Session.IdText);
                        peer.Session.Close();
                        peer.MediaClient?.Dispose();
                        if (active == peer) active = null;
                    }
                }
                client.Dispose();
            }
        }

        private async Task ReceiveMedia(Socket socket, IPAddress local)
        {
            byte[] buffer = new byte[1201];
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellation.Token);
                    MediaFrame frame;
                    try { frame = WireCodec.DecodeMedia(buffer.AsSpan(0, received.ReceivedBytes)); }
                    catch (InvalidDataException) { continue; }
                    byte[] reply = null;
                    lock (gate)
                    {
                        string id = Convert.ToHexString(frame.SessionId).ToLowerInvariant();
                        if (!peers.TryGetValue(id, out var peer) || !peer.Local.Equals(local) ||
                            !peer.Remote.Equals(((IPEndPoint)received.RemoteEndPoint).Address)) continue;
                        if (peer.Session.ObserveProbe(frame))
                        {
                            peer.MediaEndpoint = (IPEndPoint)received.RemoteEndPoint;
                            reply = WireCodec.EncodeMedia(frame with { Type = 3 });
                        }
                        else if (peer == active && !peer.Session.UsingTcp && received.RemoteEndPoint.Equals(peer.MediaEndpoint) &&
                            peer.Session.AcceptAudio(frame)) AudioReceived?.Invoke(frame);
                    }
                    if (reply != null) await socket.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint, cancellation.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
        }

        private async Task ServeMedia(TcpClient client)
        {
            Peer peer = null;
            try
            {
                var stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var hello = ControlMessage.Parse(await WireCodec.ReadControlAsync(stream, timeout.Token));
                lock (gate)
                {
                    if (hello.Type != "mediaHello" || !peers.TryGetValue(hello.SessionId, out peer) ||
                        !peer.Local.Equals(((IPEndPoint)client.Client.LocalEndPoint).Address) ||
                        !peer.Remote.Equals(((IPEndPoint)client.Client.RemoteEndPoint).Address) ||
                        !peer.Session.ConfirmTcp(hello.SessionId)) throw new InvalidDataException("Invalid BAS2 media connection");
                    peer.MediaClient = client;
                }
                await stream.WriteAsync(ControlMessage.Encode("mediaReady", hello.RequestId, peer.Session.IdText, new JsonObject { ["transport"] = "tcp" }), cancellation.Token);
                byte[] prefix = new byte[4];
                while (!cancellation.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(prefix, cancellation.Token);
                    if (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix) != 1000)
                        throw new InvalidDataException("Invalid BAS2 TCP media size");
                    byte[] packet = new byte[1000];
                    await stream.ReadExactlyAsync(packet, cancellation.Token);
                    var frame = WireCodec.DecodeMedia(packet);
                    lock (gate)
                    {
                        if (peer == active && peer.MediaClient == client && peer.Session.AcceptAudio(frame))
                            AudioReceived?.Invoke(frame);
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is OperationCanceledException ||
                error is System.Text.Json.JsonException || error is InvalidOperationException || error is ObjectDisposedException) { }
            finally
            {
                lock (gate)
                {
                    if (peer?.MediaClient == client)
                    {
                        peer.MediaClient = null;
                        peer.Session.Close();
                        peer.Client.Dispose();
                        if (active == peer) active = null;
                    }
                }
                client.Dispose();
            }
        }

        private async Task ExpireLoop()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(1000, cancellation.Token);
                    lock (gate)
                        foreach (var peer in peers.Values)
                            if (Environment.TickCount64 - peer.Session.LastHeartbeatMs > 6000) peer.Client.Dispose();
                }
            }
            catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            Task[] pending;
            lock (gate)
            {
                controlListener.Stop();
                mediaListener.Stop();
                foreach (var peer in peers.Values) { peer.Client.Dispose(); peer.MediaClient?.Dispose(); }
                foreach (var socket in mediaSockets.Values) socket.Dispose();
                pending = workers.ToArray();
            }
            await Task.WhenAll(pending);
        }
    }
}
