using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SinkLite.Core.Bas2
{
    // Discovery metadata is compatible; only bit7-capable peers may use BAS2 control/media.
    internal sealed class DiscoveryServer : IAsyncDisposable
    {
        private readonly Socket listener = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource stop = new();
        private readonly object gate = new();
        private readonly Dictionary<IPAddress, Socket> senders = new();
        private readonly string identity, name;
        private readonly int audioPort, port;
        private Task receive, announce;
        private readonly Dictionary<string, long> rate = new();
        public event Action LegacyPhone;
        public DiscoveryServer(string identity, string name, int audioPort, int port = 45678)
        { this.identity = identity; this.name = name; this.audioPort = audioPort; this.port = port; }
        public void Start()
        {
            listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            listener.Bind(new IPEndPoint(IPAddress.Any, port));
            receive = Receive(); announce = Announce();
        }
        private Socket Sender(IPAddress local)
        {
            if (senders.TryGetValue(local, out var value)) return value;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { socket.EnableBroadcast = true; socket.Bind(new IPEndPoint(local, 0)); }
            catch { socket.Dispose(); throw; }
            try {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse("224.0.0.167"), local));
            } catch (SocketException) { /* multicast may be unavailable; unicast and broadcast remain usable */ }
            senders[local] = socket; return socket;
        }
        private byte[] Offer()
        {
            byte[] offer = WifiTransport.BuildOffer(name, audioPort), id = Encoding.ASCII.GetBytes(identity);
            Array.Resize(ref offer, offer.Length + 32); id.CopyTo(offer, offer.Length - 32); offer[5] |= 128; return offer;
        }
        public bool SendOffer()
        {
            bool sent = false;
            lock (gate)
            {
                if (stop.IsCancellationRequested) return false;
                var lans = WifiLanAddresses.Read();
                foreach (var removed in senders.Keys.Where(address => !lans.Any(lan => lan.Address.Equals(address))).ToArray())
                { senders[removed].Dispose(); senders.Remove(removed); }
                foreach (var lan in lans) try {
                    var destinations = new HashSet<IPAddress> { IPAddress.Parse("224.0.0.167") };
                    WifiDiscoveryTargets.AddSubnet(destinations, lan.Address, lan.Mask, lan.Gateways);
                    var socket = Sender(lan.Address); var data = Offer();
                    foreach (var destination in destinations) try { socket.SendTo(data, new IPEndPoint(destination, port)); sent = true; } catch (SocketException) { }
                } catch (SocketException) { }
            }
            return sent;
        }
        private async Task Announce()
        {
            try { while (!stop.IsCancellationRequested) { SendOffer(); await Task.Delay(2000, stop.Token); } }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
        private async Task Receive()
        {
            byte[] buffer = new byte[1201];
            try {
                while (!stop.IsCancellationRequested) {
                    var result = await listener.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token);
                    if (!WifiTransport.TryReadHeader(buffer, result.ReceivedBytes, out var header)) continue;
                    var remote = (IPEndPoint)result.RemoteEndPoint;
                    lock (gate) {
                        if (stop.IsCancellationRequested) break;
                        long now = Environment.TickCount64; string key = remote.Address.ToString();
                        if (rate.TryGetValue(key, out long last) && now - last < 200) continue;
                        if (rate.Count > 128) rate.Clear(); rate[key] = now;
                        var lans = WifiLanAddresses.Read();
                        var local = lans.FirstOrDefault(lan => lan.Address.Equals(result.PacketInformation.Address)) ??
                            lans.FirstOrDefault(lan => WifiLanAddresses.Contains(lan, remote.Address));
                        if (local == null) continue;
                        byte[] reply = header.Type == WifiTransport.TypeIdentityQuery ? WifiIdentity.Reply(buffer, result.ReceivedBytes, identity, name, audioPort) : null;
                        if (header.Type == WifiTransport.TypeHello) { LegacyPhone?.Invoke(); reply = Offer(); }
                        if (reply != null) { reply[5] |= 128; try { Sender(local.Address).SendTo(reply, remote); } catch (SocketException) { } }
                    }
                }
            } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            lock (gate) { listener.Dispose(); foreach (var socket in senders.Values) socket.Dispose(); senders.Clear(); }
            if (receive != null) await receive.ConfigureAwait(false);
            if (announce != null) await announce.ConfigureAwait(false);
        }
    }
}
