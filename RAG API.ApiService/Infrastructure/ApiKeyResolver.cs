using RAG_API.ApiService.Auth;

namespace RAG_API.ApiService.Infrastructure;

public static class ApiKeyResolver
{
    /// <summary>
    /// Reads X-API-Key header, validates it and writes 401 if invalid.
    /// Returns false when the request should be short-circuited.
    /// </summary>
    public static bool TryResolve(
        HttpContext http,
        out ApiKeyInfo keyInfo,
        out TierInfo tier)
    {
        keyInfo = default!;
        tier    = default!;

        if (!http.Request.Headers.TryGetValue("X-API-Key", out var values)
            || string.IsNullOrWhiteSpace(values.FirstOrDefault()))
        {
            http.Response.StatusCode = 401;
            return false;
        }

        if (!AuthConfig.TryResolve(values.First()!, out keyInfo, out tier))
        {
            http.Response.StatusCode = 401;
            return false;
        }

        return true;
    }
}
