using System;

namespace SinkLite.Core
{
    /// <summary>Optional, compare-and-set volume control. Called under receiver.sync.</summary>
    internal sealed class WifiVolumeControl
    {
        private readonly uint epoch = (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1);
        private uint revision = 1, token, lastRequest;
        private int percent;
        private bool exclusive;
        private string peer;
        private bool lastAccepted;
        public bool Update(int value, bool mode, bool deviceChanged = false)
        {
            if (value == percent && mode == exclusive && !deviceChanged) return false;
            percent = value; exclusive = mode; revision++; lastAccepted = false; return true;
        }
        public byte[] State(uint ack = 0, bool accepted = false)
        {
            if (token == 0) return null;
            byte[] payload = new byte[14];
            Write(payload, 0, token); Write(payload, 4, epoch); Write(payload, 8, revision);
            payload[12] = (byte)percent; payload[13] = (byte)((exclusive ? 1 : 0) | (accepted ? 2 : 0));
            return WifiTransport.BuildPacket(WifiTransport.TypeVolumeState, 0, ack, 0, payload);
        }
        public byte[] Handle(byte[] data, int length, WifiTransport.WifiHeader header, string source, out int? requested)
        {
            requested = null;
            uint client = WifiTransport.ReadUInt32(data, 16);
            if (header.Type == WifiTransport.TypeVolumeGet)
            {
                if (length != 20 || client == 0) return null;
                if (peer != source || token != client) { peer = source; token = client; lastRequest = 0; lastAccepted = false; }
                return State();
            }
            if (length != 29 || data[28] > 100 || client == 0 || client != token || peer != source || header.Seq == 0) return null;
            if (lastRequest == 0 || unchecked((int)(header.Seq - lastRequest)) > 0)
            {
                lastRequest = header.Seq; lastAccepted = false;
                if (WifiTransport.ReadUInt32(data, 20) == epoch && WifiTransport.ReadUInt32(data, 24) == revision)
                { requested = data[28]; percent = data[28]; revision++; lastAccepted = true; }
            }
            return State(header.Seq, header.Seq == lastRequest && lastAccepted);
        }
        private static void Write(byte[] data, int at, uint value)
        { System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at, 4), value); }
    }
}
