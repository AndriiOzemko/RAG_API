using Microsoft.Extensions.Caching.Memory;

namespace RAG_API.ApiService.Auth;

/// <summary>
/// Token-bucket rate limiter backed by <see cref="IMemoryCache"/>.
/// One bucket per API key per calendar minute (UTC).
/// </summary>
public sealed class RateLimiter(IMemoryCache cache)
{
    private sealed class Bucket
    {
        public int Tokens;
        public readonly DateTime ExpiresAt = DateTime.UtcNow.AddSeconds(60);
    }

    /// <summary>
    /// Atomically adds <paramref name="tokens"/> to the current-minute bucket and
    /// returns <c>true</c> when the total stays within <paramref name="limitPerMin"/>.
    /// </summary>
    public Task<bool> TryConsumeAsync(
        string apiKey,
        int tokens,
        int limitPerMin,
        CancellationToken ct = default)
    {
        var minuteStamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
        var bucketKey   = $"rl:{apiKey}:{minuteStamp}";

        var bucket = cache.GetOrCreate(bucketKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            return new Bucket();
        })!;

        int newTotal;
        lock (bucket)
            newTotal = bucket.Tokens += tokens;

        return Task.FromResult(newTotal <= limitPerMin);
    }

    /// <summary>
    /// Returns <c>true</c> when the current bucket is already at or above the limit,
    /// without modifying the token count. Use this for a pre-flight check.
    /// </summary>
    public Task<bool> IsExceededAsync(string apiKey, int limitPerMin)
    {
        var minuteStamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
        var bucketKey   = $"rl:{apiKey}:{minuteStamp}";

        bool exceeded = cache.TryGetValue(bucketKey, out Bucket? bucket)
                        && bucket is not null
                        && bucket.Tokens >= limitPerMin;

        return Task.FromResult(exceeded);
    }

    /// <summary>Returns seconds until the current bucket expires (used for Retry-After).</summary>
    public Task<int> GetRetryAfterSecondsAsync(string apiKey)
    {
        var minuteStamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
        var bucketKey   = $"rl:{apiKey}:{minuteStamp}";

        int seconds = cache.TryGetValue(bucketKey, out Bucket? bucket) && bucket is not null
            ? Math.Max(1, (int)(bucket.ExpiresAt - DateTime.UtcNow).TotalSeconds)
            : 60;

        return Task.FromResult(seconds);
    }
}
