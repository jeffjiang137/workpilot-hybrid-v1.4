using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WorkPilot.Models;

namespace WorkPilot.Services;

public static class TextChunker
{
    public static IReadOnlyList<TextChunk> Chunk(string rawText, string fileName, string relativePath)
    {
        var text = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (text.Length == 0) return [];
        var chunks = new List<TextChunk>(); var start = 0;
        while (start < text.Length)
        {
            var end = FindEnd(text, start, IndexPolicyV13.TargetChunkTokens);
            if (end <= start) end = NextTextElement(text, start);
            var content = text[start..end];
            var estimate = EstimateTokens(content);
            chunks.Add(new(chunks.Count, start, end, estimate, content,
                SearchTextNormalizer.ExpandForFts(content), Hash(content)));
            if (chunks.Count > IndexPolicyV13.MaxChunksPerAsset)
                throw new InvalidDataException("单个资产超过 2,000 个文本块上限");
            if (end >= text.Length) break;
            var overlap = FindStartForOverlap(text, start, end, IndexPolicyV13.ChunkOverlapTokens);
            start = overlap > start ? overlap : end;
        }
        return chunks.Select(x => x with
        {
            SearchText = x.SearchText + " " + SearchTextNormalizer.ExpandForFts(fileName, true) +
                " " + SearchTextNormalizer.ExpandForFts(relativePath, true)
        }).ToArray();
    }

    public static int EstimateTokens(string text)
    {
        var count = 0; var asciiRun = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsAsciiWord(rune)) { asciiRun++; continue; }
            if (asciiRun > 0) { count += Math.Max(1, (asciiRun + 3) / 4); asciiRun = 0; }
            if (!Rune.IsWhiteSpace(rune)) count++;
        }
        if (asciiRun > 0) count += Math.Max(1, (asciiRun + 3) / 4);
        return count;
    }

    private static int FindEnd(string text, int start, int target)
    {
        var index = start; var lastLine = -1; var lastParagraph = -1;
        while (index < text.Length)
        {
            index = NextTextElement(text, index);
            var tokens = EstimateTokens(text[start..index]);
            if (text[index - 1] == '\n') { lastLine = index; if (index > 1 && text[index - 2] == '\n') lastParagraph = index; }
            if (tokens >= target)
            {
                if (lastParagraph > start && EstimateTokens(text[start..lastParagraph]) >= 600) return lastParagraph;
                if (lastLine > start && EstimateTokens(text[start..lastLine]) >= 600) return lastLine;
                return index;
            }
            if (tokens >= IndexPolicyV13.MaxChunkTokens) return index;
        }
        return text.Length;
    }

    private static int FindStartForOverlap(string text, int minimum, int end, int target)
    {
        var elements = StringInfo.ParseCombiningCharacters(text[..end]);
        var position = end;
        for (var i = elements.Length - 1; i >= 0; i--)
        {
            position = elements[i];
            if (position <= minimum || EstimateTokens(text[position..end]) >= target) break;
        }
        var line = text.LastIndexOf('\n', Math.Max(0, position - 1), position - minimum);
        return line >= minimum ? line + 1 : position;
    }

    private static int NextTextElement(string text, int index) => index +
        StringInfo.GetNextTextElementLength(text.AsSpan(index));
    private static bool IsAsciiWord(Rune rune) => rune.Value < 128 &&
        (char.IsAsciiLetterOrDigit((char)rune.Value) || rune.Value == '_');
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
