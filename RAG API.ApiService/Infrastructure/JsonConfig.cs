using System.Text.Json;
using System.Text.Json.Serialization;

namespace RAG_API.ApiService.Infrastructure;

public static class JsonConfig
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
