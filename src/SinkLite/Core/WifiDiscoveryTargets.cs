using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SinkLite.Core
{
    internal static class WifiDiscoveryTargets
    {
        public static IEnumerable<IPAddress> GetCurrent()
        {
            var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
            foreach(var lan in WifiLanAddresses.Read())
                AddSubnet(targets,lan.Address,lan.Mask,lan.Gateways);
            return targets;
        }

        internal static void AddSubnet(HashSet<IPAddress> targets, IPAddress local, IPAddress mask, IEnumerable<IPAddress> gateways)
        {
            if (!Usable(local) || mask == null || mask.AddressFamily != AddressFamily.InterNetwork) return;
            uint ip = Number(local), netmask = Number(mask), hostmask = ~netmask;
            // A contiguous mask has a suffix consisting entirely of host bits.
            if (netmask == 0 || (hostmask & (hostmask + 1)) != 0) return;
            uint network = ip & netmask, broadcast = network | hostmask;
            if (hostmask > 1 && ip != network && ip != broadcast)
                targets.Add(Address(broadcast));
            foreach (var gateway in gateways)
                if (Usable(gateway) && !gateway.Equals(local) &&
                    (Number(gateway) & netmask) == network &&
                    (hostmask <= 1 || (Number(gateway) != network && Number(gateway) != broadcast)))
                    targets.Add(gateway);
        }

        private static bool Usable(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = address.GetAddressBytes();
            return b[0] != 0 && b[0] != 127 && b[0] < 224 && !(b[0] == 169 && b[1] == 254);
        }
        private static uint Number(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }
        private static IPAddress Address(uint value) => new IPAddress(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

        internal static int Send(IEnumerable<IPAddress> targets, Action<IPAddress> send)
        {
            int sent = 0;
            foreach (var target in targets)
            {
                try { send(target); sent++; }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
            return sent;
        }
    }
}
