using Microsoft.AspNetCore.Mvc;
using RAG_API.ApiService.Metrics;

namespace RAG_API.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class HealthController(AppMetrics metrics) : ControllerBase
{
    /// <summary>GET /health — liveness probe with in-memory counters.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        status          = "healthy",
        total_requests  = metrics.TotalRequests,
        active_streams  = metrics.ActiveStreams,
        aborted_streams = metrics.AbortedStreams,
        cache_hits      = metrics.CacheHits,
    });
}
