using OpenAI;
using OpenAI.Embeddings;

namespace RAG_API.ApiService.Rag;

/// <summary>
/// Wraps OpenRouter-compatible embedding calls using the OpenAI SDK.
/// Uses text-embedding-3-small (1536 dims, available via OpenRouter).
/// </summary>
public sealed class EmbeddingService
{
    private const string EmbeddingModel = "openai/text-embedding-3-small";
    private readonly EmbeddingClient _client;
    private readonly ILogger<EmbeddingService> _log;

    public EmbeddingService(IConfiguration config, ILogger<EmbeddingService> log)
    {
        _log = log;
        var apiKey = config["OpenRouter:ApiKey"]
            ?? throw new InvalidOperationException("OpenRouter:ApiKey is required.");

        var openAiClient = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri("https://openrouter.ai/api/v1") });

        _client = openAiClient.GetEmbeddingClient(EmbeddingModel);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        _log.LogDebug("Embedding text of length {Len}", text.Length);

        var result = await _client.GenerateEmbeddingAsync(text, cancellationToken: ct);
        return result.Value.ToFloats().ToArray();
    }
}
