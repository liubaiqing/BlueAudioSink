using System.Text;
using SinkLite.Core.Bas2;

static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Reject(Action action) {
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("Accepted invalid frame");
}
byte[] id = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
var frame = new MediaFrame(1, 1, id, ulong.MaxValue, uint.MaxValue, 0x87654321, new byte[960]);
byte[] encoded = WireCodec.EncodeMedia(frame);
Check(encoded.Length == 1000);
Check(Convert.ToHexString(encoded.AsSpan(0, 40)) ==
    "42415332010128000102030405060708090A0B0C0D0E0F10FFFFFFFFFFFFFFFFFFFFFFFF21436587");
var decoded = WireCodec.DecodeMedia(encoded);
Check(decoded.Sequence == uint.MaxValue && decoded.StreamEpoch == ulong.MaxValue);
Reject(() => WireCodec.DecodeMedia(encoded.AsSpan(0, 999)));
byte[] bad = encoded.ToArray(); bad[5] = 2;
Reject(() => WireCodec.DecodeMedia(bad));
Reject(() => WireCodec.EncodeMedia(frame with { StreamEpoch = 0 }));
Reject(() => WireCodec.EncodeMedia(frame with { SessionId = new byte[16] }));
var probe = frame with { Type = 2, Flags = 0, StreamEpoch = 0, Payload = id };
Check(WireCodec.DecodeMedia(WireCodec.EncodeMedia(probe)).Payload.SequenceEqual(id));
byte[] json = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"protocol\":2}");
byte[] control = WireCodec.EncodeControl(json);
using var stream = new MemoryStream(control.Concat(control).ToArray());
Check((await WireCodec.ReadControlAsync(stream, default)).SequenceEqual(json));
Check((await WireCodec.ReadControlAsync(stream, default)).SequenceEqual(json));
Reject(() => WireCodec.EncodeControl("[]"u8));
Reject(() => WireCodec.EncodeControl(new byte[16385]));
try { await WireCodec.ReadControlAsync(new MemoryStream(new byte[] { 255, 255, 255, 127 }), default); throw new Exception("Unbounded frame accepted"); }
catch (InvalidDataException) { }
try { await WireCodec.ReadControlAsync(new MemoryStream(control[..^1]), default); throw new Exception("Truncation accepted"); }
catch (EndOfStreamException) { }
Console.WriteLine("PASS BAS2 cross-platform header vector, invalid fields, TCP boundaries and truncation");
