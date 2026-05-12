using System.Text.Json;

namespace RAG_API.ApiService.Security;

/// <summary>
/// Logs suspicious requests and responses to dedicated log files.
/// </summary>
public sealed class SuspiciousActivityLogger
{
    private readonly string _logDirectory;
    private readonly object _requestLock = new();
    private readonly object _responseLock = new();

    public SuspiciousActivityLogger(IConfiguration config)
    {
        // Default to logs directory in the application folder
        _logDirectory = config["Logging:SuspiciousActivityPath"] 
            ?? Path.Combine(AppContext.BaseDirectory, "logs");

        Directory.CreateDirectory(_logDirectory);
    }

    public void LogSuspiciousRequest(string apiKey, string tier, string input, string detectedPattern)
    {
        var entry = new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            api_key = MaskApiKey(apiKey),
            tier,
            detected_pattern = detectedPattern,
            input_length = input.Length,
            input_preview = input.Length > 200 ? input[..200] + "..." : input
        };

        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });

        lock (_requestLock)
        {
            var logFile = Path.Combine(_logDirectory, "suspicious_requests.log");
            File.AppendAllText(logFile, json + Environment.NewLine + "---" + Environment.NewLine);
        }
    }

    public void LogSuspiciousResponse(string apiKey, string tier, string model, string output)
    {
        var entry = new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            api_key = MaskApiKey(apiKey),
            tier,
            model,
            output_length = output.Length,
            output_preview = output.Length > 500 ? output[..500] + "..." : output
        };

        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });

        lock (_responseLock)
        {
            var logFile = Path.Combine(_logDirectory, "suspicious_responses.log");
            File.AppendAllText(logFile, json + Environment.NewLine + "---" + Environment.NewLine);
        }
    }

    private static string MaskApiKey(string key)
    {
        if (key.Length <= 8) return "***";
        return key[..4] + "****" + key[^4..];
    }
}
