using System.Diagnostics;
using System.Text.Json;

namespace NaxExplorer.Services;

/// <summary>Opt-in local timing capture. Samples stay in memory until the gesture ends.</summary>
internal sealed class SelectionTrace
{
    private readonly string _folder;
    private readonly List<Sample> _samples = [];
    private readonly List<ScrollSample> _scroll = [];
    private long _previousFrame;
    private long _input;
    private int _layouts;
    private readonly record struct Sample(string Stage, long Tick, double Milliseconds, int Rows, int Added, int Removed, int Selected);
    private readonly record struct ScrollSample(double Position, double Viewport, double Scrollable, double Offset, double Speed, double ActualHeight);

    private SelectionTrace(string folder) => _folder = folder;

    public static SelectionTrace? Start()
    {
        string? folder = Environment.GetEnvironmentVariable("NAX_EXPLORER_SELECTION_TRACE");
        return string.IsNullOrWhiteSpace(folder) ? null : new(folder);
    }

    public void Input() => _input = Stopwatch.GetTimestamp();
    public void Scroll(double position, double viewport, double scrollable, double offset, double speed, double actualHeight) =>
        _scroll.Add(new(position, viewport, scrollable, offset, speed, actualHeight));
    public void Layout() => _layouts++;
    public void Record(string stage, long start, int rows = 0, int added = 0, int removed = 0, int selected = 0) =>
        _samples.Add(new(stage, Stopwatch.GetTimestamp(), Stopwatch.GetElapsedTime(start).TotalMilliseconds, rows, added, removed, selected));

    public void Frame()
    {
        long now = Stopwatch.GetTimestamp();
        if (_previousFrame != 0) _samples.Add(new("frame-gap", now, Stopwatch.GetElapsedTime(_previousFrame, now).TotalMilliseconds, 0, 0, 0, 0));
        if (_input != 0) _samples.Add(new("input-age-at-frame", now, Stopwatch.GetElapsedTime(_input, now).TotalMilliseconds, 0, 0, 0, 0));
        _previousFrame = now;
    }

    public void Finish()
    {
        var capture = new { Frequency = Stopwatch.Frequency, Layouts = _layouts, Samples = _samples.ToArray(), Scroll = _scroll.ToArray() };
        string folder = _folder;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"selection-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.json"), JsonSerializer.Serialize(capture));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        });
    }
}
