using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SinkLite.Core.Bas2
{
    internal sealed record ControlMessage(string Type, uint RequestId, string SessionId, JsonElement Body)
    {
        public static ControlMessage Parse(byte[] json)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(type.GetString()) || type.GetString().Length > 48 ||
                !root.TryGetProperty("requestId", out var request) || request.ValueKind != JsonValueKind.Number || !request.TryGetUInt32(out uint id) || id == 0 ||
                !root.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid BAS2 control envelope");
            return new ControlMessage(type.GetString(), id, session.GetString(), body.Clone());
        }

        public static byte[] Encode(string type, uint requestId, string sessionId, JsonObject body)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", type);
                writer.WriteNumber("requestId", requestId);
                writer.WriteString("sessionId", sessionId);
                writer.WritePropertyName("body");
                body.WriteTo(writer);
                writer.WriteEndObject();
            }
            return WireCodec.EncodeControl(stream.ToArray());
        }
    }

    internal enum ConnectionPhase { AwaitingHello, VerifyingTransport, Ready, Streaming, Closed }

    // Called only by the connection owner, which serializes control and media events.
    internal sealed class ReceiverSession
    {
        private readonly string deviceId;
        private readonly string deviceName;
        private readonly Func<ulong> nextEpoch;
        private readonly Dictionary<uint, (string Type, string Session, string Body, byte[] Reply)> replies = new();
        private readonly Queue<uint> replyOrder = new();
        private uint lastRequest;
        private byte[] probeChallenge;
        public byte[] Id { get; } = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        public string IdText => Convert.ToHexString(Id).ToLowerInvariant();
        public ConnectionPhase Phase { get; private set; }
        public ulong StreamEpoch { get; private set; }
        public string InstanceId { get; private set; }
        public string PhoneName { get; private set; }
        public bool UsingTcp { get; private set; }
        public long LastHeartbeatMs { get; private set; }
        public int AudioPort { get; }

        public ReceiverSession(string deviceId, string deviceName, int audioPort, long nowMs, Func<ulong> nextEpoch)
        {
            this.deviceId = deviceId;
            this.deviceName = deviceName;
            this.nextEpoch = nextEpoch;
            AudioPort = audioPort;
            LastHeartbeatMs = nowMs;
        }

        public byte[] Handle(ControlMessage message, long nowMs, bool mayStart = true)
        {
            if (Phase == ConnectionPhase.Closed) throw new InvalidDataException("Session closed");
            string body = message.Body.GetRawText();
            if (replies.TryGetValue(message.RequestId, out var cached))
            {
                if (cached.Type != message.Type || cached.Session != message.SessionId || cached.Body != body) throw new InvalidDataException("Request ID reused");
                return cached.Reply;
            }
            if (Phase != ConnectionPhase.AwaitingHello && message.SessionId != IdText)
                throw new InvalidDataException("Wrong BAS2 session");
            if (lastRequest != 0 && unchecked((int)(message.RequestId - lastRequest)) <= 0)
                return Reply("error", message.RequestId, new JsonObject { ["code"] = "StaleRequest" });

            byte[] result;
            switch (message.Type)
            {
                case "hello" when Phase == ConnectionPhase.AwaitingHello:
                    if (!message.Body.TryGetProperty("protocol", out var protocol) || !protocol.TryGetInt32(out int version) || version != 2)
                        return Cache(message, Reply("error", message.RequestId, new JsonObject { ["code"] = "UpgradeRequired" }));
                    string expected = StringField(message.Body, "expectedDeviceId");
                    if (expected.Length != 0 && expected != deviceId)
                        return Cache(message, Reply("error", message.RequestId, new JsonObject { ["code"] = "IdentityMismatch" }));
                    InstanceId = StringField(message.Body, "instanceId");
                    if (InstanceId.Length != 32 || !Guid.TryParseExact(InstanceId, "N", out _)) throw new InvalidDataException("Invalid instance ID");
                    PhoneName = StringField(message.Body, "name");
                    if (PhoneName.Length > 64) throw new InvalidDataException("Phone name too long");
                    Phase = ConnectionPhase.VerifyingTransport;
                    result = Reply("welcome", message.RequestId, new JsonObject { ["protocol"] = 2, ["deviceId"] = deviceId, ["name"] = deviceName, ["audioPort"] = AudioPort });
                    break;
                case "pathConfirmed" when Phase == ConnectionPhase.VerifyingTransport:
                    string challenge = StringField(message.Body, "challenge");
                    if (probeChallenge == null || !string.Equals(challenge, Convert.ToHexString(probeChallenge), StringComparison.OrdinalIgnoreCase))
                        return Cache(message, Reply("error", message.RequestId, new JsonObject { ["code"] = "ProbeNotConfirmed" }));
                    Phase = ConnectionPhase.Ready;
                    result = Reply("pathReady", message.RequestId, new JsonObject { ["transport"] = "udp" });
                    break;
                case "heartbeat":
                    LastHeartbeatMs = nowMs;
                    result = Reply("heartbeatAck", message.RequestId, new JsonObject { ["phase"] = Phase.ToString() });
                    break;
                case "start" when Phase == ConnectionPhase.Ready || Phase == ConnectionPhase.Streaming:
                    if (!mayStart) return Cache(message, Reply("error", message.RequestId, new JsonObject { ["code"] = "Busy" }));
                    StreamEpoch = nextEpoch();
                    if (StreamEpoch == 0) throw new InvalidOperationException("Zero stream epoch");
                    Phase = ConnectionPhase.Streaming;
                    result = Reply("startAck", message.RequestId, new JsonObject { ["streamEpoch"] = StreamEpoch.ToString("x16") });
                    break;
                case "stop" when Phase == ConnectionPhase.Ready || Phase == ConnectionPhase.Streaming:
                    StreamEpoch = 0;
                    Phase = ConnectionPhase.Ready;
                    result = Reply("stopAck", message.RequestId, new JsonObject());
                    break;
                default:
                    return Cache(message, Reply("error", message.RequestId, new JsonObject { ["code"] = "InvalidStateOrMessage" }));
            }
            LastHeartbeatMs = nowMs;
            return Cache(message, result);
        }

        private byte[] Cache(ControlMessage message, byte[] result)
        {
            lastRequest = message.RequestId;
            replies.Add(message.RequestId, (message.Type, message.SessionId, message.Body.GetRawText(), result));
            replyOrder.Enqueue(message.RequestId);
            if (replyOrder.Count > 128) replies.Remove(replyOrder.Dequeue());
            return result;
        }

        public bool ObserveProbe(MediaFrame frame)
        {
            if (Phase != ConnectionPhase.VerifyingTransport || frame.Type != 2 || !frame.SessionId.AsSpan().SequenceEqual(Id)) return false;
            probeChallenge = (byte[])frame.Payload.Clone();
            return true;
        }

        public bool ConfirmTcp(string sessionId)
        {
            if (Phase != ConnectionPhase.VerifyingTransport || sessionId != IdText) return false;
            UsingTcp = true;
            Phase = ConnectionPhase.Ready;
            return true;
        }

        public bool AcceptAudio(MediaFrame frame) =>
            Phase == ConnectionPhase.Streaming && frame.Type == 1 && frame.StreamEpoch == StreamEpoch && frame.SessionId.AsSpan().SequenceEqual(Id);

        public void Close() { Phase = ConnectionPhase.Closed; StreamEpoch = 0; probeChallenge = null; replies.Clear(); replyOrder.Clear(); }

        private byte[] Reply(string type, uint request, JsonObject body) => ControlMessage.Encode(type, request, IdText, body);
        private static string StringField(JsonElement body, string name)
        {
            if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Missing BAS2 field " + name);
            return value.GetString();
        }
    }
}
