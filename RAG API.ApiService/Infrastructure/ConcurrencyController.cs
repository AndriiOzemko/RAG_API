namespace RAG_API.ApiService.Infrastructure;

/// <summary>
/// Controls concurrent LLM API calls to prevent rate limit exhaustion.
/// </summary>
public sealed class ConcurrencyController
{
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger<ConcurrencyController> _log;

    public ConcurrencyController(IConfiguration config, ILogger<ConcurrencyController> log)
    {
        // Default to 20 concurrent LLM calls
        var maxConcurrent = config.GetValue<int>("LLM:MaxConcurrentCalls", 20);
        _semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _log = log;

        _log.LogInformation("ConcurrencyController initialized with max {Max} concurrent LLM calls", maxConcurrent);
    }

    /// <summary>
    /// Acquires a slot for an LLM call. Must be released after use.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        return new SemaphoreReleaser(_semaphore);
    }

    public int CurrentCount => _semaphore.CurrentCount;

    private sealed class SemaphoreReleaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public SemaphoreReleaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Release();
                _disposed = true;
            }
        }
    }
}
