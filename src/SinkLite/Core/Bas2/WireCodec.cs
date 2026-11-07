using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SinkLite.Core.Bas2
{
    internal sealed record MediaFrame(byte Type, byte Flags, byte[] SessionId, ulong StreamEpoch, uint Sequence, uint TimestampUs, byte[] Payload);

    internal static class WireCodec
    {
        public const int HeaderSize = 40;
        public const int MaxControlBytes = 16384;

        public static byte[] EncodeMedia(MediaFrame frame)
        {
            Validate(frame);
            byte[] bytes = new byte[HeaderSize + frame.Payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x32534142);
            bytes[4] = frame.Type;
            bytes[5] = frame.Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), HeaderSize);
            frame.SessionId.CopyTo(bytes, 8);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), frame.StreamEpoch);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), frame.Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), frame.TimestampUs);
            frame.Payload.CopyTo(bytes, HeaderSize);
            return bytes;
        }

        public static MediaFrame DecodeMedia(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < HeaderSize || bytes.Length > 1000 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x32534142 ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6)) != HeaderSize)
                throw new InvalidDataException("Invalid BAS2 media header");
            var frame = new MediaFrame(bytes[4], bytes[5], bytes.Slice(8, 16).ToArray(),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(32)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(36)), bytes.Slice(HeaderSize).ToArray());
            Validate(frame);
            return frame;
        }

        private static void Validate(MediaFrame frame)
        {
            if (frame.SessionId == null || frame.SessionId.Length != 16 || Array.TrueForAll(frame.SessionId, b => b == 0) || frame.Payload == null)
                throw new InvalidDataException("Invalid BAS2 session");
            bool audio = frame.Type == 1 && frame.StreamEpoch != 0 && frame.Payload.Length == 960 && (frame.Flags & ~1) == 0;
            bool probe = (frame.Type == 2 || frame.Type == 3) && frame.StreamEpoch == 0 && frame.Flags == 0 && frame.Payload.Length == 16;
            if (!audio && !probe) throw new InvalidDataException("Invalid BAS2 media fields");
        }

        public static byte[] EncodeControl(ReadOnlySpan<byte> json)
        {
            ValidateControl(json.ToArray());
            byte[] bytes = new byte[4 + json.Length];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, json.Length);
            json.CopyTo(bytes.AsSpan(4));
            return bytes;
        }

        public static async Task<byte[]> ReadControlAsync(Stream stream, CancellationToken token)
        {
            byte[] prefix = new byte[4];
            await stream.ReadExactlyAsync(prefix, token);
            int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length < 1 || length > MaxControlBytes) throw new InvalidDataException("Invalid BAS2 control size");
            byte[] json = new byte[length];
            await stream.ReadExactlyAsync(json, token);
            ValidateControl(json);
            return json;
        }

        private static void ValidateControl(byte[] json)
        {
            if (json.Length < 1 || json.Length > MaxControlBytes) throw new InvalidDataException("Invalid BAS2 control size");
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected BAS2 control object");
            }
            catch (JsonException error) { throw new InvalidDataException("Invalid BAS2 control JSON", error); }
        }
    }
}
