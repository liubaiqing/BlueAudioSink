using System;
using System.Buffers.Binary;
using System.Text;

namespace SinkLite.Core
{
    /// <summary>
    /// Wi-Fi 模式的协议常量与包头编解码，严格对应 docs/02-protocol.md。
    ///
    /// 所有多字节整数均为小端；包头固定 16 字节：
    ///   magic(4) type(1) flags(1) reserved(2) seq(4) timestampUs(4)
    /// </summary>
    internal static class WifiTransport
    {
        /// <summary>发现 / 控制端口（PC 广播 OFFER，手机监听）。</summary>
        public const int ControlPort = 45678;

        /// <summary>默认音频端口（手机发 AUDIO，PC 监听）。</summary>
        public const int DefaultAudioPort = 45679;

        public const int HeaderSize = 16;

        /// <summary>magic \"BAS1\" 的小端 u32 值。</summary>
        public const uint Magic = 0x31534142;

        // type 定义（协议第 2 节）
        public const byte TypeAudio = 1;
        public const byte TypeStart = 2;
        public const byte TypeStop = 3;
        public const byte TypePing = 4;
        public const byte TypePong = 5;
        public const byte TypeOffer = 6;
        public const byte TypeHello = 7;
        public const byte TypeVolumeGet = 8, TypeVolumeSet = 9, TypeVolumeState = 10;

        /// <summary>flags bit0：断流标记。</summary>
        public const byte FlagDiscontinuity = 0x01;

        // 音频格式（协议第 3 节，已按 5 ms 包修订：960 字节 payload，避免 IP 分片）
        public const int SampleRate = 48000;
        public const int Channels = 2;
        public const int BitsPerSample = 16;
        public const int BytesPerFrame = Channels * BitsPerSample / 8;
        public const int FrameMs = 5;
        public const int FramesPerPacket = SampleRate * FrameMs / 1000;
        public const int PayloadBytes = FramesPerPacket * BytesPerFrame;
        public const int PacketBytes = HeaderSize + PayloadBytes;

        /// <summary>解析后的包头。</summary>
        public struct WifiHeader
        {
            public byte Type;
            public byte Flags;
            public uint Seq;
            public uint TimestampUs;
        }

        /// <summary>校验 magic 并解析包头；长度不足或 magic 不符返回 false。</summary>
        public static bool TryReadHeader(byte[] data, int length, out WifiHeader header)
        {
            header = default(WifiHeader);
            if (data == null || length < HeaderSize)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(data, 0, 4)) != Magic)
            {
                return false;
            }

            header.Type = data[4];
            header.Flags = data[5];
            header.Seq = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(data, 8, 4));
            header.TimestampUs = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(data, 12, 4));
            return true;
        }

        /// <summary>构造一个完整协议包（16 字节头 + payload）。</summary>
        public static byte[] BuildPacket(byte type, byte flags, uint seq, uint timestampUs, byte[] payload)
        {
            int payloadLength = payload == null ? 0 : payload.Length;
            byte[] packet = new byte[HeaderSize + payloadLength];

            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(packet, 0, 4), Magic);
            packet[4] = type;
            packet[5] = flags;
            // packet[6]、packet[7] 即 reserved，保持为 0
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(packet, 8, 4), seq);
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(packet, 12, 4), timestampUs);

            if (payloadLength > 0)
            {
                Buffer.BlockCopy(payload, 0, packet, HeaderSize, payloadLength);
            }

            return packet;
        }

        /// <summary>
        /// OFFER：设备名 UTF-8 + 0x00 + 4 字节音频端口(u32 小端)。
        /// 每 2 秒向 255.255.255.255:45678 广播一次。
        /// </summary>
        public static byte[] BuildOffer(string deviceName, int audioPort)
        {
            string nameText = string.IsNullOrEmpty(deviceName) ? "BlueAudioSink" : deviceName;
            byte[] name = Encoding.UTF8.GetBytes(nameText);
            if (name.Length > 63)
            {
                // 按 UTF-8 码点边界截断到 63 字节，避免切出半个字符。
                int cut = 63;
                while (cut > 0 && (name[cut] & 0xC0) == 0x80)
                {
                    cut--;
                }
                byte[] trimmed = new byte[cut];
                Buffer.BlockCopy(name, 0, trimmed, 0, cut);
                name = trimmed;
            }

            byte[] payload = new byte[name.Length + 1 + 4];
            if (name.Length > 0)
            {
                Buffer.BlockCopy(name, 0, payload, 0, name.Length);
            }
            payload[name.Length] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(
                new Span<byte>(payload, name.Length + 1, 4),
                unchecked((uint)audioPort));

            return BuildPacket(TypeOffer, 0, 0, 0, payload);
        }

        /// <summary>
        /// PONG：payload 原样回填 PING 的 T1；header.timestampUs 为手机发送时刻 T2。
        /// 该重载主要用于兼容"手机主动 PING"的旧实现，正常流程由 PC 发起 PING。
        /// </summary>
        public static byte[] BuildPong(uint echoUs, uint sendUs)
        {
            byte[] payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(payload, 0, 4), echoUs);
            return BuildPacket(TypePong, 0, 0, sendUs, payload);
        }

        /// <summary>读取 payload 中的 u32；越界返回 0。</summary>
        public static uint ReadUInt32(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length)
            {
                return 0;
            }
            return BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(data, offset, 4));
        }

        /// <summary>读取以 0 结尾的 UTF-8 字符串（允许没有结尾 0）。</summary>
        public static string ReadUtf8Z(byte[] data, int offset, int length)
        {
            if (data == null || offset < 0 || length <= 0 || offset >= data.Length)
            {
                return null;
            }

            int max = Math.Min(length, data.Length - offset);
            int end = offset;
            while (end < offset + max && data[end] != 0)
            {
                end++;
            }

            if (end <= offset)
            {
                return null;
            }

            return Encoding.UTF8.GetString(data, offset, end - offset);
        }
    }
}
