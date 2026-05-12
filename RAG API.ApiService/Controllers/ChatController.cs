using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RAG_API.ApiService.Auth;
using RAG_API.ApiService.Database;
using RAG_API.ApiService.Infrastructure;
using RAG_API.ApiService.Metrics;
using RAG_API.ApiService.Models;
using RAG_API.ApiService.Rag;
using RAG_API.ApiService.Security;

namespace RAG_API.ApiService.Controllers;

[ApiController]
[Route("chat")]
[ApiKeyAuth]
public sealed class ChatController(
    RagService rag,
    RateLimiter limiter,
    AppMetrics metrics,
    VectorDb db,
    PromptInjectionDetector injectionDetector,
    SuspiciousActivityLogger suspiciousLogger,
    ConcurrencyController concurrency,
    ILogger<ChatController> logger) : ControllerBase
{
    /// <summary>POST /chat/stream — SSE streaming RAG response.</summary>
    [HttpPost("stream")]
    public async Task StreamAsync([FromBody] ChatRequest req)
    {
        var keyInfo = HttpContext.GetApiKeyInfo();
        var tier    = HttpContext.GetTierInfo();

        // ── Input validation: length & prompt injection ──────────────────────
        var validationError = injectionDetector.ValidateInput(req.Message, keyInfo.Key, out var detectedPattern);
        if (validationError != null)
        {
            if (detectedPattern != null)
            {
                // Log suspicious request
                suspiciousLogger.LogSuspiciousRequest(keyInfo.Key, keyInfo.Tier, req.Message, detectedPattern);
            }

            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                title = "Bad Request",
                status = 400,
                detail = validationError
            }, cancellationToken: HttpContext.RequestAborted);
            return;
        }

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
        bool streamStarted = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ChatStreamResult? doneResult = null;
        var accumulatedAnswer = new System.Text.StringBuilder();

        try
        {
            // ── Acquire concurrency slot ─────────────────────────────────────
            logger.LogDebug("Acquiring concurrency slot for {Key}", keyInfo.Key);
            using var slot = await concurrency.AcquireAsync(ct);
            logger.LogDebug("Concurrency slot acquired for {Key}", keyInfo.Key);

            await foreach (var result in rag.StreamAsync(
                req.Message, tier,
                disconnected: () => HttpContext.RequestAborted.IsCancellationRequested
                    ? Task.FromResult(true)
                    : Task.FromResult(false),
                ct: ct))
            {
                streamStarted = true;

                if (HttpContext.RequestAborted.IsCancellationRequested)
                {
                    aborted = true;
                    metrics.IncrementAborted();
                    logger.LogInformation("Client disconnected during stream for {Key}", keyInfo.Key);
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
                    // Accumulate tokens for output filtering
                    if (!string.IsNullOrEmpty(result.Content))
                        accumulatedAnswer.Append(result.Content);

                    var tokenPayload = JsonSerializer.Serialize(new
                    {
                        type    = "token",
                        content = result.Content,
                    }, JsonConfig.Default);

                    await SseWriter.WriteAsync(Response, tokenPayload, ct);
                }
            }

            // ── Output filtering: check for system prompt leaks ──────────────
            bool outputFiltered = false;
            if (doneResult is not null && !doneResult.CacheHit && accumulatedAnswer.Length > 0)
            {
                if (injectionDetector.ContainsSystemPromptLeak(accumulatedAnswer.ToString()))
                {
                    outputFiltered = true;
                    suspiciousLogger.LogSuspiciousResponse(
                        keyInfo.Key, keyInfo.Tier, doneResult.ModelUsed, accumulatedAnswer.ToString());
                }
            }

            // ── Log cost (ONLY if request completed successfully, not aborted) ───
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
                    Fallback:     doneResult.FallbackUsed,
                    OutputFiltered: outputFiltered));

                logger.LogInformation(
                    "Request completed: {Key}, model={Model}, tokens={Tokens}, cost=${Cost:F4}",
                    keyInfo.Key, doneResult.ModelUsed, 
                    doneResult.InputTokens + doneResult.OutputTokens, doneResult.CostUsd);
            }
            else if (aborted)
            {
                // Do NOT log aborted requests to usage tracker
                logger.LogInformation("Request aborted, not logging usage: {Key}", keyInfo.Key);
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true;
            metrics.IncrementAborted();
            logger.LogInformation("Request cancelled (client disconnect): {Key}", keyInfo.Key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during stream for {Key}", keyInfo.Key);
            throw;
        }
        finally
        {
            metrics.DecrementActive();
            logger.LogDebug("Released active stream slot for {Key}", keyInfo.Key);
        }
    }
}

