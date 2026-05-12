using System.Text.RegularExpressions;

namespace RAG_API.ApiService.Security;

/// <summary>
/// Detects potential prompt injection attacks in user input.
/// </summary>
public sealed partial class PromptInjectionDetector
{
    private readonly ILogger<PromptInjectionDetector> _log;
    private const int MaxInputLength = 4000;

    // Suspicious patterns that may indicate prompt injection attempts
    private static readonly string[] SuspiciousPatterns =
    [
        @"ignore\s+(previous|all|prior|earlier)\s+instructions?",
        @"system\s*:\s*",
        @"<\|im_start\|>",
        @"<\|im_end\|>",
        @"</s>",
        @"<s>",
        @"###\s*instruction",
        @"human\s*:\s*ignore",
        @"disregard\s+(all|previous|above)\s+",
        @"forget\s+(your|all|previous)\s+",
        @"you\s+are\s+now\s+",
        @"new\s+instructions?\s*:",
        @"override\s+system",
        @"reveal\s+your\s+(system\s+)?prompt",
        @"show\s+me\s+your\s+(system\s+)?prompt",
        @"what\s+(are|is)\s+your\s+(system\s+)?instructions?",
    ];

    private static readonly Regex[] CompiledPatterns = SuspiciousPatterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    public PromptInjectionDetector(ILogger<PromptInjectionDetector> log)
    {
        _log = log;
    }

    /// <summary>
    /// Validates user input for length and suspicious patterns.
    /// </summary>
    /// <returns>Null if valid, error message if invalid.</returns>
    public string? ValidateInput(string input, string apiKey, out string? detectedPattern)
    {
        detectedPattern = null;

        // Check length
        if (input.Length > MaxInputLength)
        {
            _log.LogWarning("Input length exceeded for API key {Key}: {Length} chars", 
                apiKey, input.Length);
            return $"Input exceeds maximum length of {MaxInputLength} characters.";
        }

        // Check for suspicious patterns
        foreach (var pattern in CompiledPatterns)
        {
            var match = pattern.Match(input);
            if (match.Success)
            {
                detectedPattern = match.Value;
                _log.LogWarning(
                    "Suspicious pattern detected in input from API key {Key}: '{Pattern}'",
                    apiKey, detectedPattern);
                return "Suspicious input detected. Please rephrase your question.";
            }
        }

        return null; // Valid
    }

    /// <summary>
    /// Checks output for leaked system prompt fragments.
    /// </summary>
    public bool ContainsSystemPromptLeak(string output)
    {
        // Look for common system prompt indicators in the response
        var leakPatterns = new[]
        {
            @"you are a helpful q&?a assistant",
            @"answer.*using only.*context",
            @"if the answer is not in the context",
            @"be concise and accurate",
            @"<user_query>",
            @"</user_query>",
            @"system message",
            @"system prompt",
            @"assistant instructions",
        };

        foreach (var pattern in leakPatterns)
        {
            if (Regex.IsMatch(output, pattern, RegexOptions.IgnoreCase))
            {
                _log.LogWarning("Potential system prompt leak detected in output: '{Pattern}'", pattern);
                return true;
            }
        }

        return false;
    }
}
