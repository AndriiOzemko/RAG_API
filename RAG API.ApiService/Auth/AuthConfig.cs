namespace RAG_API.ApiService.Auth;

public record ApiKeyInfo(string Key, string Tier);

public record TierInfo(int RateLimitTokensPerMin, IReadOnlyList<string> Models);

public static class AuthConfig
{
    // 3 hard-coded API keys with their tiers
    private static readonly Dictionary<string, string> ApiKeys = new(StringComparer.Ordinal)
    {
        ["demo-free-key-abc123"]        = "demo-free",
        ["demo-pro-key-def456"]         = "demo-pro",
        ["demo-enterprise-key-ghi789"]  = "demo-enterprise",
    };

    private static readonly Dictionary<string, TierInfo> Tiers = new(StringComparer.Ordinal)
    {
        ["demo-free"] = new TierInfo(
            RateLimitTokensPerMin: 5_000,
            Models:
            [
                "meta-llama/llama-3.1-8b-instruct",
                "google/gemini-flash-1.5",
                "meta-llama/llama-3.2-3b-instruct:free",
            ]),

        ["demo-pro"] = new TierInfo(
            RateLimitTokensPerMin: 20_000,
            Models:
            [
                "mistralai/mistral-small-3.1-24b-instruct",
                "meta-llama/llama-3.1-70b-instruct",
                "meta-llama/llama-3.1-8b-instruct",
            ]),

        ["demo-enterprise"] = new TierInfo(
            RateLimitTokensPerMin: 100_000,
            Models:
            [
                "anthropic/claude-3.7-sonnet",
                "openai/gpt-4o",
                "anthropic/claude-3-haiku",
            ]),
    };

    public static bool TryResolve(string apiKey, out ApiKeyInfo info, out TierInfo tier)
    {
        if (ApiKeys.TryGetValue(apiKey, out var tierName) && Tiers.TryGetValue(tierName, out tier!))
        {
            info = new ApiKeyInfo(apiKey, tierName);
            return true;
        }

        info = default!;
        tier = default!;
        return false;
    }

    public static TierInfo GetTier(string tierName) => Tiers[tierName];
}
