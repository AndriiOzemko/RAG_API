using System.Buffers;
using System.IO.Pipelines;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RAG_API.Web;

public class RagApiClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Streams SSE events from POST /chat/stream.
    /// Uses PipeReader to avoid synchronous stream reads (required for Blazor WASM).
    /// </summary>
    public async IAsyncEnumerable<SseEvent> StreamChatAsync(
        string message,
        string apiKey,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/chat/stream");
        request.Headers.Add("X-API-Key", apiKey);
        request.Content = JsonContent.Create(new { message });

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            yield return new SseEvent("error", null, body, null, null, false, null);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var pipe = PipeReader.Create(stream);

        while (!ct.IsCancellationRequested)
        {
            ReadResult result;
            try   { result = await pipe.ReadAsync(ct); }
            catch { break; }

            var buffer = result.Buffer;

            while (TryReadLine(ref buffer, out var lineSeq))
            {
                var line = Encoding.UTF8.GetString(lineSeq);
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var json = line["data: ".Length..].Trim();
                if (string.IsNullOrEmpty(json)) continue;

                SseEvent? evt = null;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString() ?? "";

                    if (type == "token")
                    {
                        var content = root.TryGetProperty("content", out var c) ? c.GetString() : null;
                        evt = new SseEvent("token", content, null, null, null, false, null);
                    }
                    else if (type == "done")
                    {
                        int inputTokens = 0, outputTokens = 0;
                        if (root.TryGetProperty("usage", out var usage))
                        {
                            inputTokens  = usage.GetProperty("input_tokens").GetInt32();
                            outputTokens = usage.GetProperty("output_tokens").GetInt32();
                        }
                        double cost     = root.TryGetProperty("cost_usd",  out var c2) ? c2.GetDouble()  : 0;
                        bool cacheHit   = root.TryGetProperty("cache_hit", out var ch) && ch.GetBoolean();
                        string[]? sources = null;
                        if (root.TryGetProperty("sources", out var s))
                            sources = s.Deserialize<string[]>(JsonOpts);

                        ChunkInfo[]? chunks = null;
                        if (root.TryGetProperty("chunks", out var ch2))
                            chunks = ch2.Deserialize<ChunkInfo[]>(JsonOpts);

                        evt = new SseEvent("done", null, null, inputTokens, outputTokens, cacheHit, sources, chunks) { CostUsd = cost };
                    }
                }
                catch { /* malformed frame — skip */ }

                if (evt is not null)
                    yield return evt;
            }

            pipe.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted) break;
        }

        await pipe.CompleteAsync();
    }

    /// <summary>Tries to slice one \n-terminated line from the buffer.</summary>
    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (reader.TryReadTo(out line, (byte)'\n'))
        {
            buffer = buffer.Slice(reader.Position);
            return true;
        }
        line = default;
        return false;
    }

    public async Task<HealthInfo?> GetHealthAsync(CancellationToken ct = default)
        => await httpClient.GetFromJsonAsync<HealthInfo>("/health", ct);

    public async Task<UsageSummary?> GetUsageTodayAsync(CancellationToken ct = default)
        => await httpClient.GetFromJsonAsync<UsageSummary>("/usage/today", ct);

    public async Task<List<ModelBreakdown>?> GetUsageBreakdownAsync(CancellationToken ct = default)
        => await httpClient.GetFromJsonAsync<List<ModelBreakdown>>("/usage/breakdown", ct);

    public async Task<bool> RebuildIndexAsync(CancellationToken ct = default)
    {
        var response = await httpClient.PostAsync("/index/rebuild", null, ct);
        return response.IsSuccessStatusCode;
    }
}

public record SseEvent(
    string Type,
    string? Content,
    string? Error,
    int? InputTokens,
    int? OutputTokens,
    bool CacheHit,
    string[]? Sources,
    ChunkInfo[]? Chunks = null)
{
    public double CostUsd { get; init; }
}

public record ChunkInfo(string Id, string Content);

public record HealthInfo(
    string Status,
    long TotalRequests,
    long ActiveStreams,
    long AbortedStreams,
    long CacheHits);

public record UsageSummary(
    int TotalRequests,
    long TotalTokens,
    double TotalCostUsd,
    double CacheHitRate,
    double AvgLatencyMs);

public record ModelBreakdown(
    string Model,
    int Requests,
    double TotalCostUsd,
    double CacheHitRate,
    double AvgLatencyMs);
