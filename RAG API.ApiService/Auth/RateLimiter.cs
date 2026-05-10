using System.Collections.Concurrent;

namespace RAG_API.ApiService.Auth;

/// <summary>
/// Sliding-window token-per-minute rate limiter per API key.
/// </summary>
public sealed class RateLimiter
{
    private sealed class Window
    {
        public long WindowStartTicks;
        public int TokensUsed;
    }

    private static readonly long TicksPerMinute = TimeSpan.FromMinutes(1).Ticks;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns true when the request is allowed; increments usage by <paramref name="tokens"/>.
    /// Returns false when the limit is exceeded.
    /// </summary>
    public bool TryConsume(string apiKey, int tokens, int limitPerMin)
    {
        var window = _windows.GetOrAdd(apiKey, _ => new Window { WindowStartTicks = DateTime.UtcNow.Ticks });

        long now = DateTime.UtcNow.Ticks;

        lock (window)
        {
            // Reset window if a minute has elapsed
            if (now - window.WindowStartTicks >= TicksPerMinute)
            {
                window.WindowStartTicks = now;
                window.TokensUsed = 0;
            }

            if (window.TokensUsed + tokens > limitPerMin)
                return false;

            window.TokensUsed += tokens;
            return true;
        }
    }
}
