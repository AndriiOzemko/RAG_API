using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace RAG_API.ApiService.Rag;

/// <summary>
/// Splits source documents (Markdown or PDF) into overlapping text chunks.
/// Fixed-size chunking with overlap to preserve context at boundaries.
/// </summary>
public sealed class DocumentChunker
{
    private const int ChunkSize    = 500; // characters
    private const int ChunkOverlap = 100;  // characters

    public IReadOnlyList<TextChunk> ChunkMarkdown(string filePath)
    {
        var text = File.ReadAllText(filePath);
        // Strip Markdown syntax characters for cleaner chunk content
        text = Markdig.Markdown.ToPlainText(text);
        return SplitText(text, Path.GetFileName(filePath));
    }

    public IReadOnlyList<TextChunk> ChunkPdf(string filePath)
    {
        using var doc = PdfDocument.Open(filePath);
        var sb = new System.Text.StringBuilder();
        foreach (Page page in doc.GetPages())
            sb.Append(page.Text).Append('\n');

        return SplitText(sb.ToString(), Path.GetFileName(filePath));
    }

    private static List<TextChunk> SplitText(string text, string sourceName)
    {
        var chunks = new List<TextChunk>();
        int start = 0;
        int index = 0;

        while (start < text.Length)
        {
            int end = Math.Min(start + ChunkSize, text.Length);

            string content = text[start..end].Trim();
            if (content.Length > 0)
            {
                chunks.Add(new TextChunk(
                    Id: $"chunk_{index}",
                    Content: content,
                    Source: sourceName,
                    Index: index));
                index++;
            }

            start = end - ChunkOverlap;
            if (start <= 0 || end == text.Length) break;
        }

        return chunks;
    }
}

public record TextChunk(string Id, string Content, string Source, int Index);
