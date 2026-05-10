using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RAG_API.ApiService.Auth;

namespace RAG_API.ApiService.Infrastructure;

/// <summary>
/// Action filter that validates the X-API-Key header and populates
/// HttpContext.Items with the resolved <see cref="ApiKeyInfo"/> and <see cref="TierInfo"/>.
/// Apply [ApiKeyAuth] to any action or controller that requires authentication.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ApiKeyAuthAttribute : Attribute, IAsyncActionFilter
{
    public const string KeyInfoItem  = "ApiKeyInfo";
    public const string TierInfoItem = "TierInfo";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;

        if (!http.Request.Headers.TryGetValue("X-API-Key", out var values)
            || string.IsNullOrWhiteSpace(values.FirstOrDefault()))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Missing X-API-Key header." });
            return;
        }

        if (!AuthConfig.TryResolve(values.First()!, out var keyInfo, out var tier))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid API key." });
            return;
        }

        http.Items[KeyInfoItem]  = keyInfo;
        http.Items[TierInfoItem] = tier;

        await next();
    }
}

/// <summary>Convenience extension to read resolved key/tier from HttpContext.Items.</summary>
public static class HttpContextAuthExtensions
{
    public static ApiKeyInfo GetApiKeyInfo(this HttpContext http)
        => (ApiKeyInfo)http.Items[ApiKeyAuthAttribute.KeyInfoItem]!;

    public static TierInfo GetTierInfo(this HttpContext http)
        => (TierInfo)http.Items[ApiKeyAuthAttribute.TierInfoItem]!;
}
