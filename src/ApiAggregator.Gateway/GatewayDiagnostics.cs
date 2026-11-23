using System.Diagnostics;
using ApiAggregator.Engine.Config;

namespace ApiAggregator.Gateway;

/// <summary>
/// Runtime numbers for the benchmark and for operations (docs/guides/benchmarking.md): memory, garbage collections,
/// threads, requests in flight. Numbers only: no request, caller or config values. Off unless
/// <c>Gateway:Diagnostics:Enabled</c>, and readable by authors and publishers only (the "log-reader" policy).
/// </summary>
internal static class GatewayDiagnostics
{
    private static long _exceptions;

    public static void MapRuntimeDiagnostics(this WebApplication app, string pattern, string policy, InFlightRequests inFlight)
    {
        // Counted only while diagnostics are on: one increment per exception thrown anywhere in the process.
        AppDomain.CurrentDomain.FirstChanceException += (_, _) => Interlocked.Increment(ref _exceptions);

        // Forcing a full collection makes the benchmark's leak check exact. It pauses the process, so it is allowed only
        // in Development, and only when switched on as well.
        var allowForcedGc = app.Environment.IsDevelopment() && app.Configuration.GetValue("Gateway:Diagnostics:AllowForcedGc", false);

        app.MapGet(pattern, async (bool? gc, IConfigStore store, CancellationToken ct) =>
        {
            if (gc == true && allowForcedGc)
            {
                // Compact the large-object heap too, so the reading is what is really held, not free gaps in it.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            var info = GC.GetGCMemoryInfo();
            using var process = Process.GetCurrentProcess();
            ThreadPool.GetMinThreads(out var minWorkers, out _);
            var snapshot = await store.GetSnapshotAsync(ct);
            return Results.Ok(new
            {
                managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                heapAfterLastGcBytes = info.HeapSizeBytes,
                committedBytes = info.TotalCommittedBytes,
                fragmentedBytes = info.FragmentedBytes,
                totalAllocatedBytes = GC.GetTotalAllocatedBytes(),
                gen0Collections = GC.CollectionCount(0),
                gen1Collections = GC.CollectionCount(1),
                gen2Collections = GC.CollectionCount(2),
                gcPauseTimePercent = info.PauseTimePercentage,
                workingSetBytes = process.WorkingSet64,
                threads = process.Threads.Count,
                threadPoolThreads = ThreadPool.ThreadCount,
                threadPoolMinWorkers = minWorkers,
                threadPoolQueue = ThreadPool.PendingWorkItemCount,
                exceptionsThrown = Interlocked.Read(ref _exceptions),
                requestsInFlight = inFlight.Current,
                requestsTurnedAway = inFlight.RejectedCount,
                recipes = snapshot.Recipes.Count,
                configVersion = snapshot.Version,
                forcedGc = gc == true && allowForcedGc
            });
        }).RequireAuthorization(policy);
    }
}

/// <summary>How many /bff requests are being answered right now, and how many were turned away as too many.</summary>
internal sealed class InFlightRequests
{
    private int _current;
    private long _rejected;

    public int Current => Volatile.Read(ref _current);

    public long RejectedCount => Interlocked.Read(ref _rejected);

    public int Enter() => Interlocked.Increment(ref _current);

    public void Leave() => Interlocked.Decrement(ref _current);

    public void Rejected() => Interlocked.Increment(ref _rejected);
}
