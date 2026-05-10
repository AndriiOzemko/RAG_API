using System.Text;

namespace RAG_API.ApiService.Infrastructure;

public static class SseWriter
{
    public static async Task WriteAsync(HttpResponse response, string data, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }
}
