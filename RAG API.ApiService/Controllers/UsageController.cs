using Microsoft.AspNetCore.Mvc;
using RAG_API.ApiService.Database;

namespace RAG_API.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class UsageController(VectorDb db) : ControllerBase
{
    /// <summary>GET /usage/today — token and cost summary for today.</summary>
    [HttpGet("today")]
    public async Task<IActionResult> TodayAsync(CancellationToken ct)
    {
        var summary = await db.GetTodayUsageAsync(ct);
        return Ok(summary);
    }

    /// <summary>GET /usage/breakdown — per-model breakdown for today.</summary>
    [HttpGet("breakdown")]
    public async Task<IActionResult> BreakdownAsync(CancellationToken ct)
    {
        var breakdown = await db.GetBreakdownAsync(ct);
        return Ok(breakdown);
    }
}
