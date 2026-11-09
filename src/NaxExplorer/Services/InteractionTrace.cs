using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace NaxExplorer.Services;

/// <summary>Opt-in local interaction timings. Writing never waits on the UI thread.</summary>
internal static class InteractionTrace
{
    private sealed record Sample(long Tick, string Stage, double Milliseconds, object? Data);
    private static readonly Channel<Sample>? Samples = Start();
    public static bool Enabled => Samples is not null;

    private static Channel<Sample>? Start()
    {
        string? folder = Environment.GetEnvironmentVariable("NAX_EXPLORER_INTERACTION_TRACE");
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var channel = Channel.CreateBounded<Sample>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                using var output = new StreamWriter(Path.Combine(folder, $"interaction-{Environment.ProcessId}.jsonl")) { AutoFlush = true };
                await foreach (var sample in channel.Reader.ReadAllAsync())
                    await output.WriteLineAsync(JsonSerializer.Serialize(sample));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        });
        return channel;
    }

    public static void Record(string stage, long start = 0, object? data = null)
    {
        if (Samples is null) return;
        long tick = Stopwatch.GetTimestamp();
        Samples.Writer.TryWrite(new(tick, stage, start == 0 ? 0 : Stopwatch.GetElapsedTime(start, tick).TotalMilliseconds, data));
    }
}
