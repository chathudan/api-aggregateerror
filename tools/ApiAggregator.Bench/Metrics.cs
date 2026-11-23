using System.Collections.Concurrent;

namespace ApiAggregator.Bench;

/// <summary>
/// Response times in buckets about 2% wide, from 1 µs to 2 minutes. Lock-free, so 200 workers can record at once;
/// percentiles are read from the bucket counts (good to about 2%).
/// </summary>
public sealed class Histogram
{
    private const double Growth = 1.02;
    private static readonly int BucketCount = (int)Math.Ceiling(Math.Log(120_000_000) / Math.Log(Growth)) + 1;
    private readonly long[] _buckets = new long[BucketCount];
    private long _count;
    private long _maxMicros;

    public long Count => Interlocked.Read(ref _count);

    public void Record(double micros)
    {
        var index = micros <= 1 ? 0 : Math.Min(BucketCount - 1, (int)(Math.Log(micros) / Math.Log(Growth)));
        Interlocked.Increment(ref _buckets[index]);
        Interlocked.Increment(ref _count);
        var value = (long)micros;
        long seen;
        while (value > (seen = Interlocked.Read(ref _maxMicros)) && Interlocked.CompareExchange(ref _maxMicros, value, seen) != seen) { }
    }

    /// <summary>The response time (ms) that this share of requests stayed under, e.g. 0.99 for p99.</summary>
    public double PercentileMs(double share)
    {
        var total = Count;
        if (total == 0) return 0;
        var target = (long)Math.Ceiling(total * share);
        long seen = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            seen += Interlocked.Read(ref _buckets[i]);
            if (seen >= target) return Math.Pow(Growth, i + 1) / 1000.0;
        }
        return MaxMs;
    }

    public double MaxMs => Interlocked.Read(ref _maxMicros) / 1000.0;
}

/// <summary>Everything counted in one phase of the run.</summary>
public sealed class PhaseStats(string name)
{
    public string Name { get; } = name;
    public Histogram Latency { get; } = new();
    public ConcurrentDictionary<int, long> Statuses { get; } = new();
    public ConcurrentDictionary<string, long> Codes { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<string> Unexpected { get; } = new();
    public double Seconds { get; set; }
    public int Workers { get; set; }
    private long _unexpected, _partial, _cacheHits, _shed;

    public long UnexpectedCount => Interlocked.Read(ref _unexpected);
    public long Partial => Interlocked.Read(ref _partial);
    public long CacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>Requests the gateway turned away because it was full (503 "overloaded"): protection, not failure.</summary>
    public long Shed => Interlocked.Read(ref _shed);

    public double RequestsPerSecond => Seconds > 0 ? Latency.Count / Seconds : 0;

    public void Count(int status, string? code, bool partial, bool cacheHit)
    {
        Statuses.AddOrUpdate(status, 1, (_, n) => n + 1);
        if (code is not null) Codes.AddOrUpdate(code, 1, (_, n) => n + 1);
        if (partial) Interlocked.Increment(ref _partial);
        if (cacheHit) Interlocked.Increment(ref _cacheHits);
        if (code == "overloaded") Interlocked.Increment(ref _shed);
    }

    /// <summary>Something the gateway should never do here. Keeps the first 25 examples for the report.</summary>
    public void Flag(string what)
    {
        if (Interlocked.Increment(ref _unexpected) <= 25) Unexpected.Enqueue(what);
    }
}

/// <summary>One reading of the gateway process.</summary>
public sealed record Sample(
    double AtSeconds, string Phase, double HeapMb, double WorkingSetMb, double CommittedMb, int Threads, int ThreadPoolThreads,
    long ThreadPoolQueue, int InFlight, int Gen2, long Exceptions, double AllocatedMb, double CpuPercent, bool ForcedGc);
