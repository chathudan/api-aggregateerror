namespace ApiAggregator.Engine.Logging.Live;

/// <summary>
/// The most recent log lines of this process, kept in memory. Thread-safe and bounded: when it is full
/// the oldest line is dropped. Readers ask for "everything after seq N" and can wait for new lines.
/// </summary>
public sealed class LiveLogBuffer(int capacity = 2000)
{
    private readonly object _lock = new();
    private readonly Queue<LiveLogEntry> _entries = new();
    private long _latestSeq;

    // Completed (and replaced by a fresh one) when a line is added while a reader waits on it, which wakes the reader.
    // Only then: under load the gateway logs tens of thousands of lines a second, mostly with nobody watching.
    private TaskCompletionSource _signal = NewSignal();
    private bool _signalTaken;

    public int Capacity { get; } = Math.Max(1, capacity);

    /// <summary>Seq of the newest line, or 0 when nothing has been logged yet.</summary>
    public long LatestSeq { get { lock (_lock) return _latestSeq; } }

    /// <summary>Stores the line (giving it the next Seq) and wakes anyone waiting for new lines.</summary>
    public void Add(LiveLogEntry entry)
    {
        TaskCompletionSource? toWake = null;
        lock (_lock)
        {
            _entries.Enqueue(entry with { Seq = ++_latestSeq });
            while (_entries.Count > Capacity) _entries.Dequeue();

            if (_signalTaken)
            {
                toWake = _signal;
                _signal = NewSignal();
                _signalTaken = false;
            }
        }
        toWake?.TrySetResult(); // outside the lock: continuations run on the thread pool anyway
    }

    /// <summary>Lines with Seq greater than <paramref name="after"/>, oldest first, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<LiveLogEntry> Read(long after, int max = 500)
    {
        lock (_lock)
            return _entries.Where(e => e.Seq > after).Take(Math.Clamp(max, 1, Capacity)).ToList();
    }

    /// <summary>
    /// Completes as soon as a line newer than <paramref name="after"/> exists, or when the timeout passes
    /// (whichever is first). Throws OperationCanceledException if the token is cancelled.
    /// </summary>
    public async Task WaitForNewAsync(long after, TimeSpan timeout, CancellationToken ct)
    {
        Task signal;
        lock (_lock)
        {
            if (_latestSeq > after) return;
            signal = _signal.Task; // taken inside the lock, so an Add can't slip between the check and the wait
            _signalTaken = true;
        }

        try { await signal.WaitAsync(timeout, ct); }
        catch (TimeoutException) { } // nothing arrived: that is a normal answer for a long poll
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
