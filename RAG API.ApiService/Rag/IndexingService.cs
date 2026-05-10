using RAG_API.ApiService.Database;

namespace RAG_API.ApiService.Rag;

/// <summary>
/// Orchestrates: chunk document → embed each chunk → upsert to VectorDb.
/// </summary>
public sealed class IndexingService
{
    private readonly DocumentChunker _chunker;
    private readonly EmbeddingService _embedder;
    private readonly VectorDb _db;
    private readonly ILogger<IndexingService> _log;
    private readonly IWebHostEnvironment _env;

    public IndexingService(
        DocumentChunker chunker,
        EmbeddingService embedder,
        VectorDb db,
        ILogger<IndexingService> log,
        IWebHostEnvironment env)
    {
        _chunker = chunker;
        _embedder = embedder;
        _db = db;
        _log = log;
        _env = env;
    }

    public async Task RebuildAsync(CancellationToken ct = default)
    {
        var dataDir = Path.Combine(_env.ContentRootPath, "data");
        var mdFile = Path.Combine(dataDir, "source.md");

        if (!File.Exists(mdFile))
        {
            _log.LogWarning("Source document not found at {Path}", mdFile);
            return;
        }

        _log.LogInformation("Clearing existing chunks...");
        await _db.ClearChunksAsync(ct);

        var chunks = _chunker.ChunkMarkdown(mdFile);
        _log.LogInformation("Indexing {Count} chunks from {File}", chunks.Count, mdFile);

        int done = 0;
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var embedding = await _embedder.EmbedAsync(chunk.Content, ct);
            await _db.UpsertChunkAsync(chunk.Id, chunk.Content, chunk.Source, chunk.Index, embedding, ct);
            done++;
            if (done % 10 == 0)
                _log.LogInformation("Indexed {Done}/{Total} chunks", done, chunks.Count);
        }

        _log.LogInformation("Indexing complete — {Total} chunks stored.", chunks.Count);
    }
}
