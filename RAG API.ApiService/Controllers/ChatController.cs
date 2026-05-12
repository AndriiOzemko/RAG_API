using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RAG_API.ApiService.Auth;
using RAG_API.ApiService.Database;
using RAG_API.ApiService.Infrastructure;
using RAG_API.ApiService.Metrics;
using RAG_API.ApiService.Models;
using RAG_API.ApiService.Rag;

namespace RAG_API.ApiService.Controllers;

[ApiController]
[Route("chat")]
[ApiKeyAuth]
public sealed class ChatController(
    RagService rag,
    RateLimiter limiter,
    AppMetrics metrics,
    VectorDb db) : ControllerBase
{
    /// <summary>POST /chat/stream — SSE streaming RAG response.</summary>
    [HttpPost("stream")]
    public async Task StreamAsync([FromBody] ChatRequest req)
    {
        var keyInfo = HttpContext.GetApiKeyInfo();
        var tier    = HttpContext.GetTierInfo();

        // ── SSE headers ──────────────────────────────────────────────────────
        Response.Headers.ContentType  = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection   = "keep-alive";

        metrics.IncrementRequests();
        metrics.IncrementActive();

        var ct = HttpContext.RequestAborted;

        // ── Pre-flight rate limit check (no tokens consumed yet) ─────────────
        if (await limiter.IsExceededAsync(keyInfo.Key, tier.RateLimitTokensPerMin))
        {
            int retryAfter = await limiter.GetRetryAfterSecondsAsync(keyInfo.Key);
            Response.Headers["Retry-After"] = retryAfter.ToString();
            Response.StatusCode = 429;
            await SseWriter.WriteAsync(Response, System.Text.Json.JsonSerializer.Serialize(new
            {
                type        = "error",
                code        = 429,
                message     = "Rate limit exceeded.",
                retry_after = retryAfter,
            }, JsonConfig.Default), ct);
            metrics.DecrementActive();
            return;
        }

        bool aborted = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ChatStreamResult? doneResult = null;

        try
        {
            await foreach (var result in rag.StreamAsync(
                req.Message, tier,
                disconnected: () => HttpContext.RequestAborted.IsCancellationRequested
                    ? Task.FromResult(true)
                    : Task.FromResult(false),
                ct: ct))
            {
                if (HttpContext.RequestAborted.IsCancellationRequested)
                {
                    aborted = true;
                    metrics.IncrementAborted();
                    break;
                }

                if (result.Type == "done")
                {
                    doneResult = result;
                    if (result.CacheHit)
                        metrics.IncrementCacheHits();

                    // ── Rate limit: consume actual tokens after LLM response ──
                    int totalTokens = result.InputTokens + result.OutputTokens;
                    if (totalTokens > 0)
                    {
                        bool allowed = await limiter.TryConsumeAsync(
                            keyInfo.Key, totalTokens, tier.RateLimitTokensPerMin, ct);

                        if (!allowed)
                        {
                            int retryAfter = await limiter.GetRetryAfterSecondsAsync(keyInfo.Key);
                            // Signal the client via a rate-limit SSE event before closing
                            var rlPayload = JsonSerializer.Serialize(new
                            {
                                type        = "error",
                                code        = 429,
                                message     = "Rate limit exceeded.",
                                retry_after = retryAfter,
                            }, JsonConfig.Default);
                            await SseWriter.WriteAsync(Response, rlPayload, ct);

                            Response.Headers["Retry-After"] = retryAfter.ToString();
                            return;
                        }
                    }

                    var donePayload = JsonSerializer.Serialize(new
                    {
                        type      = "done",
                        usage     = new { input_tokens = result.InputTokens, output_tokens = result.OutputTokens },
                        cost_usd  = result.CostUsd,
                        cache_hit = result.CacheHit,
                        sources   = result.Sources,
                        chunks    = result.Chunks.Select(c => new { id = c.Id, content = c.Content }),
                        model_used = result.ModelUsed,
                        fallback_used = result.FallbackUsed,
                    }, JsonConfig.Default);

                    await SseWriter.WriteAsync(Response, donePayload, ct);
                }
                else
                {
                    var tokenPayload = JsonSerializer.Serialize(new
                    {
                        type    = "token",
                        content = result.Content,
                    }, JsonConfig.Default);

                    await SseWriter.WriteAsync(Response, tokenPayload, ct);
                }
            }

            // ── Log cost ─────────────────────────────────────────────────────
            if (doneResult is not null && !aborted)
            {
                _ = db.LogUsageAsync(new UsageEntry(
                    ApiKey:       keyInfo.Key,
                    Tier:         keyInfo.Tier,
                    Model:        doneResult.ModelUsed,
                    InputTokens:  doneResult.InputTokens,
                    OutputTokens: doneResult.OutputTokens,
                    CostUsd:      doneResult.CostUsd,
                    CacheHit:     doneResult.CacheHit,
                    LatencyMs:    (int)sw.ElapsedMilliseconds,
                    Aborted:      false,
                    Fallback:     doneResult.FallbackUsed));
            }
            else if (aborted)
            {
                _ = db.LogUsageAsync(new UsageEntry(
                    keyInfo.Key, keyInfo.Tier, tier.Models[0],
                    0, 0, 0, false, (int)sw.ElapsedMilliseconds, Aborted: true, Fallback: false));
            }
        }
        catch (OperationCanceledException)
        {
            metrics.IncrementAborted();
        }
        finally
        {
            metrics.DecrementActive();
        }
    }
}

