using System.Net;
using System.Text.Json;
using SinkLite.Core.Bas2;

internal static class DeviceProbe
{
    public static async Task Run(string[] args)
    {
        bool tcp = args.Contains("--block-udp");
        string result = args.SkipWhile(item => item != "--result").Skip(1).First();
        int duration = int.Parse(args.SkipWhile(item => item != "--seconds").Skip(1).FirstOrDefault() ?? "45");
        await using var server = new ControlServer(IPAddress.Any, 45778, 45779, new string('a', 32), "BAS2-device-probe", !tcp);
        long received = 0, invalid = 0, discontinuities = 0;
        long firstAt = 0, lastAt = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        server.AudioReceived += frame => {
            if (Interlocked.Increment(ref received) == 1) firstAt = clock.ElapsedMilliseconds;
            lastAt = clock.ElapsedMilliseconds;
            for (int i = 0; i < frame.Payload.Length; i++)
                if (frame.Payload[i] != (byte)((frame.Sequence * 31L + i) % 251)) { Interlocked.Increment(ref invalid); break; }
            if (frame.Flags != 0) Interlocked.Increment(ref discontinuities);
        };
        server.Start();
        Console.WriteLine($"READY actual LAN host TCP45778 media45779 UDP-probe={!tcp}; no audio output");
        await Task.Delay(TimeSpan.FromSeconds(duration));
        var report = new { transport = tcp ? "tcp-fallback" : "udp", received, invalid, discontinuities, firstAtMs = firstAt, lastAtMs = lastAt };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(result))!);
        await File.WriteAllTextAsync(result, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        if (received == 0 || invalid != 0) Environment.ExitCode = 1;
    }
}
