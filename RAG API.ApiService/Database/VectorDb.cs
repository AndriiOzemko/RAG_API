using Microsoft.Data.SqlClient;

namespace RAG_API.ApiService.Database;

/// <summary>
/// Manages the MS SQL schema for chunks + vector similarity search.
/// Uses SQL Server 2025 native VECTOR type.
/// </summary>
public sealed class VectorDb
{
    private readonly string _connectionString;
    private const int EmbeddingDim = 1536; // text-embedding-3-small

    // Aspire injects the connection string into IConfiguration under
    // ConnectionStrings:VectorDb via the resource reference in AppHost.
    public VectorDb(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("VectorDb")
            ?? throw new InvalidOperationException("ConnectionString 'VectorDb' is required.");
    }

    // ── Schema ─────────────────────────────────────────────────────────────

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Usage log table
        await ExecAsync(conn, """
            IF OBJECT_ID('dbo.UsageLog','U') IS NULL
            CREATE TABLE dbo.UsageLog (
                Id              BIGINT IDENTITY PRIMARY KEY,
                CreatedAt       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                ApiKey          NVARCHAR(200) NOT NULL,
                Tier            NVARCHAR(50)  NOT NULL,
                Model           NVARCHAR(200) NOT NULL,
                InputTokens     INT NOT NULL DEFAULT 0,
                OutputTokens    INT NOT NULL DEFAULT 0,
                CostUsd         FLOAT NOT NULL DEFAULT 0,
                CacheHit        BIT NOT NULL DEFAULT 0,
                LatencyMs       INT NOT NULL DEFAULT 0,
                Aborted         BIT NOT NULL DEFAULT 0
            );
            """, ct);

        // Chunks table with native VECTOR column
        await ExecAsync(conn, $"""
            IF OBJECT_ID('dbo.Chunks','U') IS NULL
            CREATE TABLE dbo.Chunks (
                Id          NVARCHAR(100) NOT NULL PRIMARY KEY,
                Content     NVARCHAR(MAX) NOT NULL,
                Source      NVARCHAR(500) NOT NULL,
                ChunkIndex  INT NOT NULL,
                Embedding   VECTOR({EmbeddingDim}) NULL
            );
            """, ct);

        // Semantic cache table
        await ExecAsync(conn, $"""
            IF OBJECT_ID('dbo.SemanticCache','U') IS NULL
            CREATE TABLE dbo.SemanticCache (
                Id              BIGINT IDENTITY PRIMARY KEY,
                QueryEmbedding  VECTOR({EmbeddingDim}) NOT NULL,
                QueryText       NVARCHAR(2000) NOT NULL,
                Answer          NVARCHAR(MAX)  NOT NULL,
                Sources         NVARCHAR(MAX)  NOT NULL,
                CreatedAt       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            """, ct);
    }

    // ── Indexing ────────────────────────────────────────────────────────────

    public async Task ClearChunksAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "DELETE FROM dbo.Chunks;", ct);
    }

    public async Task UpsertChunkAsync(
        string id, string content, string source, int index,
        float[] embedding, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var embJson = FloatsToVectorJson(embedding);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            MERGE dbo.Chunks AS target
            USING (SELECT @Id AS Id) AS src ON target.Id = src.Id
            WHEN MATCHED THEN
                UPDATE SET Content = @Content, Source = @Source, ChunkIndex = @ChunkIndex,
                           Embedding = CAST(@Embedding AS VECTOR(1536))
            WHEN NOT MATCHED THEN
                INSERT (Id, Content, Source, ChunkIndex, Embedding)
                VALUES (@Id, @Content, @Source, @ChunkIndex, CAST(@Embedding AS VECTOR(1536)));
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Content", content);
        cmd.Parameters.AddWithValue("@Source", source);
        cmd.Parameters.AddWithValue("@ChunkIndex", index);
        cmd.Parameters.AddWithValue("@Embedding", embJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Vector Search ───────────────────────────────────────────────────────

    public async Task<List<ChunkResult>> SearchAsync(
        float[] queryEmbedding, int topK = 3, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var embJson = FloatsToVectorJson(queryEmbedding);

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 30;
        cmd.CommandText = $"""
            SELECT TOP (@TopK)
                Id,
                Content,
                Source,
                ChunkIndex,
                VECTOR_DISTANCE('cosine', Embedding, CAST(@Embedding AS VECTOR(1536))) AS Distance
            FROM dbo.Chunks
            WHERE Embedding IS NOT NULL
            ORDER BY Distance ASC;
            """;
        cmd.Parameters.AddWithValue("@TopK", topK);
        cmd.Parameters.AddWithValue("@Embedding", embJson);

        var results = new List<ChunkResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ChunkResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetDouble(4)));
        }

        return results;
    }

    // ── Semantic Cache ──────────────────────────────────────────────────────

    /// <summary>Returns cached answer if cosine distance &lt; threshold.</summary>
    public async Task<CacheEntry?> FindCacheAsync(
        float[] queryEmbedding, double threshold = 0.06, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var embJson = FloatsToVectorJson(queryEmbedding);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1
                QueryText, Answer, Sources,
                VECTOR_DISTANCE('cosine', QueryEmbedding, CAST(@Embedding AS VECTOR(1536))) AS Distance
            FROM dbo.SemanticCache
            ORDER BY Distance ASC;
            """;
        cmd.Parameters.AddWithValue("@Embedding", embJson);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            double dist = reader.GetDouble(3);
            if (dist < threshold)
            {
                return new CacheEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return null;
    }

    public async Task SaveCacheAsync(
        float[] queryEmbedding, string queryText,
        string answer, IEnumerable<string> sources, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var embJson = FloatsToVectorJson(queryEmbedding);
        var sourcesStr = string.Join(",", sources);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.SemanticCache (QueryEmbedding, QueryText, Answer, Sources)
            VALUES (CAST(@Embedding AS VECTOR(1536)), @QueryText, @Answer, @Sources);
            """;
        cmd.Parameters.AddWithValue("@Embedding", embJson);
        cmd.Parameters.AddWithValue("@QueryText", queryText);
        cmd.Parameters.AddWithValue("@Answer", answer);
        cmd.Parameters.AddWithValue("@Sources", sourcesStr);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Usage Logging ───────────────────────────────────────────────────────

    public async Task LogUsageAsync(UsageEntry entry, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.UsageLog
                (ApiKey, Tier, Model, InputTokens, OutputTokens, CostUsd, CacheHit, LatencyMs, Aborted)
            VALUES
                (@ApiKey, @Tier, @Model, @Input, @Output, @Cost, @CacheHit, @Latency, @Aborted);
            """;
        cmd.Parameters.AddWithValue("@ApiKey", entry.ApiKey);
        cmd.Parameters.AddWithValue("@Tier", entry.Tier);
        cmd.Parameters.AddWithValue("@Model", entry.Model);
        cmd.Parameters.AddWithValue("@Input", entry.InputTokens);
        cmd.Parameters.AddWithValue("@Output", entry.OutputTokens);
        cmd.Parameters.AddWithValue("@Cost", entry.CostUsd);
        cmd.Parameters.AddWithValue("@CacheHit", entry.CacheHit);
        cmd.Parameters.AddWithValue("@Latency", entry.LatencyMs);
        cmd.Parameters.AddWithValue("@Aborted", entry.Aborted);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<UsageSummary> GetTodayUsageAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*)                AS TotalRequests,
                ISNULL(SUM(CAST(InputTokens AS BIGINT) + CAST(OutputTokens AS BIGINT)), 0) AS TotalTokens,
                ISNULL(SUM(CostUsd), 0) AS TotalCostUsd,
                ISNULL(AVG(CAST(CacheHit AS FLOAT)), 0)  AS CacheHitRate,
                ISNULL(AVG(CAST(LatencyMs AS FLOAT)), 0) AS AvgLatencyMs
            FROM dbo.UsageLog
            WHERE CAST(CreatedAt AS DATE) = CAST(SYSUTCDATETIME() AS DATE);
            """;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            return new UsageSummary(
                r.GetInt32(0), r.GetInt64(1),
                r.GetDouble(2), r.GetDouble(3), r.GetDouble(4));
        }

        return new UsageSummary(0, 0, 0, 0, 0);
    }

    public async Task<List<ModelBreakdown>> GetBreakdownAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                Model,
                COUNT(*)                AS Requests,
                ISNULL(SUM(CostUsd), 0) AS TotalCostUsd,
                ISNULL(AVG(CAST(CacheHit AS FLOAT)), 0)  AS CacheHitRate,
                ISNULL(AVG(CAST(LatencyMs AS FLOAT)), 0) AS AvgLatencyMs
            FROM dbo.UsageLog
            WHERE CAST(CreatedAt AS DATE) = CAST(SYSUTCDATETIME() AS DATE)
            GROUP BY Model
            ORDER BY TotalCostUsd DESC;
            """;

        var rows = new List<ModelBreakdown>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new ModelBreakdown(
                r.GetString(0), r.GetInt32(1),
                r.GetDouble(2), r.GetDouble(3), r.GetDouble(4)));
        }

        return rows;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Serialize float[] to the JSON array format SQL Server VECTOR expects.</summary>
    private static string FloatsToVectorJson(float[] values)
        => "[" + string.Join(",", values.Select(v => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture))) + "]";
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record ChunkResult(string Id, string Content, string Source, int ChunkIndex, double Distance);

public record CacheEntry(string QueryText, string Answer, string[] Sources);

public record UsageEntry(
    string ApiKey, string Tier, string Model,
    int InputTokens, int OutputTokens,
    double CostUsd, bool CacheHit, int LatencyMs, bool Aborted);

public record UsageSummary(
    int TotalRequests, long TotalTokens,
    double TotalCostUsd, double CacheHitRate, double AvgLatencyMs);

public record ModelBreakdown(
    string Model, int Requests,
    double TotalCostUsd, double CacheHitRate, double AvgLatencyMs);
