namespace Hubwright.Desktop;

/// <summary>
/// The work in progress, for the strip at the top of the page: each running action has a label
/// (usually the button that started it) and the latest progress line. Used on the UI thread only.
/// </summary>
public static class Activity
{
    private static readonly List<Entry> Running = [];

    /// <summary>Raised when an action starts, reports progress or ends.</summary>
    public static event Action? Changed;

    public static bool IsBusy => Running.Count > 0;

    /// <summary>The most recent action and how many others are running with it.</summary>
    public static (string Label, string? Detail, DateTimeOffset Started, int Others)? Current
        => Running.Count == 0 ? null : (Running[^1].Label, Running[^1].Detail, Running[^1].Started, Running.Count - 1);

    /// <summary>Starts an action; dispose the result when it ends.</summary>
    public static IDisposable Begin(string label)
    {
        var entry = new Entry(label.TrimEnd('…', '.', ' '));
        Running.Add(entry);
        Changed?.Invoke();
        return entry;
    }

    /// <summary>The latest progress line of the most recent action (the same text the page shows).</summary>
    public static void Report(string detail)
    {
        if (Running.Count > 0)
        {
            Running[^1].Detail = detail;
            Changed?.Invoke();
        }
    }

    private sealed class Entry(string label) : IDisposable
    {
        public string Label { get; } = label;

        public string? Detail { get; set; }

        public DateTimeOffset Started { get; } = DateTimeOffset.Now;

        public void Dispose()
        {
            if (Running.Remove(this))
            {
                Changed?.Invoke();
            }
        }
    }
}
