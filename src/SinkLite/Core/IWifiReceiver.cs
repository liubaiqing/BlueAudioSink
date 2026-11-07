using System;
using System.Collections.Generic;
using System.Net;
namespace SinkLite.Core
{
    internal interface IWifiReceiver : IDisposable
    {
        event Action RemoteVolumeChanged;
        bool Start(); void Stop(); bool SendOffer(IEnumerable<IPAddress> targets = null);
        void SetBufferBase(int milliseconds); void SetOutputOptions(WifiOutputOptions options); void RefreshOutput();
        IReadOnlyList<WifiPeer> ListPeers(); bool SelectPeer(string id);
        void PrepareReconnect(IPEndPoint peer, string name); void ResetNetworkPeers(); WifiSnapshot GetSnapshot();
    }
}
