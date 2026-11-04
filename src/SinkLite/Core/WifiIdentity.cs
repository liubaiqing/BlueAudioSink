using System;
using System.IO;
using System.Text;

namespace SinkLite.Core
{
    internal static class WifiIdentity
    {
        private static readonly Lazy<string> identity = new Lazy<string>(() => LoadOrCreate(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueAudioSink", "wifi-device-id")));
        public static string Current => identity.Value;
        internal static string LoadOrCreate(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) return Read(path);
            string value = Guid.NewGuid().ToString("N");
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, value, new UTF8Encoding(false));
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { return Read(path); }
                return value;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static string Read(string path)
        {
            string value = File.ReadAllText(path).Trim();
            if (!Valid(value)) throw new InvalidDataException("电脑身份文件无效，请恢复 wifi-device-id 备份或移除损坏文件后重新添加设备");
            return value;
        }
        internal static bool Valid(string value)
        {
            if (value == null || value.Length != 32) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        public static byte[] Reply(byte[] data, int length, string id, string name, int port)
        {
            if (length != 64 || !Valid(id)) return null;
            bool wildcard = true;
            for (int i=32;i<64;i++) if (data[i] != 0) wildcard = false;
            if (!wildcard && Encoding.ASCII.GetString(data,32,32) != id) return null;
            byte[] offer = WifiTransport.BuildOffer(name,port);
            int nameLength = offer.Length - WifiTransport.HeaderSize - 5;
            byte[] payload = new byte[52+nameLength];
            Buffer.BlockCopy(data,16,payload,0,16);
            Encoding.ASCII.GetBytes(id,0,32,payload,16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48,4),(uint)port);
            Buffer.BlockCopy(offer,16,payload,52,nameLength);
            return WifiTransport.BuildPacket(WifiTransport.TypeIdentityReply,0,0,0,payload);
        }
    }
}
