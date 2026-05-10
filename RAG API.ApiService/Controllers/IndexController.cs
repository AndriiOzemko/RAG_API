using Microsoft.AspNetCore.Mvc;
using RAG_API.ApiService.Rag;

namespace RAG_API.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class IndexController(IndexingService indexer) : ControllerBase
{
    /// <summary>POST /index/rebuild — re-index source document.</summary>
    [HttpPost("rebuild")]
    public async Task<IActionResult> RebuildAsync()
    {
        _ = Task.Run(() => indexer.RebuildAsync());
        return Accepted(new { status = "indexing started" });
    }
}
