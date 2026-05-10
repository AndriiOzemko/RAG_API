using Microsoft.AspNetCore.OpenApi;
using RAG_API.ApiService.Auth;
using RAG_API.ApiService.Database;
using RAG_API.ApiService.Metrics;
using RAG_API.ApiService.Rag;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ── Service Defaults (Aspire) ────────────────────────────────────────────────
builder.AddServiceDefaults();

// ── Core services ────────────────────────────────────────────────────────────
builder.Services.AddSingleton<VectorDb>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<DocumentChunker>();
builder.Services.AddSingleton<IndexingService>();
builder.Services.AddSingleton<RagService>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<AppMetrics>();

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title       = "RAG API";
        doc.Info.Version     = "v1";
        doc.Info.Description = "Document Q&A RAG service with semantic vector search and SSE streaming.";
        doc.Info.Contact     = new() { Name = "RAG API" };
        return Task.CompletedTask;
    });
});

// ── CORS (allow Blazor WASM frontend) ────────────────────────────────────────
builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ── Middleware ───────────────────────────────────────────────────────────────
app.UseCors();
app.UseExceptionHandler();

// ── OpenAPI / Swagger UI / Scalar UI ─────────────────────────────────────────
app.MapOpenApi();                         // /openapi/v1.json

app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "RAG API v1");
    options.RoutePrefix = "swagger";      // /swagger
    options.DocumentTitle = "RAG API – Swagger UI";
    options.EnableTryItOutByDefault();
    options.DisplayRequestDuration();
});

app.MapScalarApiReference(options =>
{
    options.Title             = "RAG API";
    options.Theme             = ScalarTheme.DeepSpace;
    options.DefaultHttpClient = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
});                                       // /scalar/v1

// ── Ensure DB schema on startup ──────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<VectorDb>().EnsureSchemaAsync();
}

// ── Controllers ───────────────────────────────────────────────────────────────
app.MapControllers();
app.MapDefaultEndpoints();

app.Run();

