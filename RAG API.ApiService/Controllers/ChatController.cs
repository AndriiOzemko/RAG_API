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
        var keyInfo = AuthConfig.AnonymousKeyInfo;
        var tier    = AuthConfig.AnonymousTier;

        // ── SSE headers ──────────────────────────────────────────────────────
        Response.Headers.ContentType  = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection   = "keep-alive";

        metrics.IncrementRequests();
        metrics.IncrementActive();

        bool aborted = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ChatStreamResult? doneResult = null;
        var ct = HttpContext.RequestAborted;

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

                    var donePayload = JsonSerializer.Serialize(new
                    {
                        type      = "done",
                        usage     = new { input_tokens = result.InputTokens, output_tokens = result.OutputTokens },
                        cost_usd  = result.CostUsd,
                        cache_hit = result.CacheHit,
                        sources   = result.Sources,
                        chunks    = result.Chunks.Select(c => new { id = c.Id, content = c.Content }),
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
                    Model:        tier.Models[0],
                    InputTokens:  doneResult.InputTokens,
                    OutputTokens: doneResult.OutputTokens,
                    CostUsd:      doneResult.CostUsd,
                    CacheHit:     doneResult.CacheHit,
                    LatencyMs:    (int)sw.ElapsedMilliseconds,
                    Aborted:      false));
            }
            else if (aborted)
            {
                _ = db.LogUsageAsync(new UsageEntry(
                    keyInfo.Key, keyInfo.Tier, tier.Models[0],
                    0, 0, 0, false, (int)sw.ElapsedMilliseconds, Aborted: true));
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

