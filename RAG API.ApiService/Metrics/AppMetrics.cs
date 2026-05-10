using System.Collections.Concurrent;

namespace RAG_API.ApiService.Metrics;

/// <summary>
/// In-memory metrics counters visible on GET /health.
/// </summary>
public sealed class AppMetrics
{
    private long _totalRequests;
    private long _activeStreams;
    private long _abortedStreams;
    private long _cacheHits;

    public long TotalRequests   => Volatile.Read(ref _totalRequests);
    public long ActiveStreams   => Volatile.Read(ref _activeStreams);
    public long AbortedStreams  => Volatile.Read(ref _abortedStreams);
    public long CacheHits       => Volatile.Read(ref _cacheHits);

    public void IncrementRequests()   => Interlocked.Increment(ref _totalRequests);
    public void IncrementActive()     => Interlocked.Increment(ref _activeStreams);
    public void DecrementActive()     => Interlocked.Decrement(ref _activeStreams);
    public void IncrementAborted()    => Interlocked.Increment(ref _abortedStreams);
    public void IncrementCacheHits()  => Interlocked.Increment(ref _cacheHits);
}
