using System.Globalization;
using System.Text;

namespace WorkPilot.Services;

public static class SearchTextNormalizer
{
    public static string Normalize(string value)
    {
        var source = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var output = new StringBuilder(source.Length);
        foreach (var rune in source.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control && rune.Value is not '\n' and not '\t') continue;
            output.Append(rune.ToString());
        }
        return output.ToString();
    }

    public static string ExpandForFts(string value, bool pathField = false)
    {
        var normalized = Normalize(value);
        if (pathField) normalized = normalized.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ')
            .Replace('/', ' ').Replace('\\', ' ');
        var output = new StringBuilder(normalized.Length * 2);
        var cjkRun = new List<string>(); var tokens = 0;
        void Flush()
        {
            for (var i = 0; i < cjkRun.Count && tokens < 20_000; i++, tokens++) output.Append(cjkRun[i]).Append(' ');
            for (var i = 0; i + 1 < cjkRun.Count && tokens < 20_000; i++, tokens++) output.Append(cjkRun[i]).Append(cjkRun[i + 1]).Append(' ');
            cjkRun.Clear();
        }
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (IsCjk(rune)) cjkRun.Add(rune.ToString());
            else { Flush(); output.Append(rune.ToString()); }
        }
        Flush(); return output.ToString();
    }

    public static string BuildMatchQuery(string value, out bool simplified)
    {
        var elements = StringInfo.GetTextElementEnumerator(Normalize(value.Trim()));
        var text = new StringBuilder(); var count = 0;
        while (elements.MoveNext() && count < 200) { text.Append(elements.GetTextElement()); count++; }
        var words = ExpandForFts(text.ToString(), true).Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Take(33).ToArray();
        simplified = words.Length > 32;
        return string.Join(" AND ", words.Take(32).Select(x => $"\"{x.Replace("\"", "\"\"")}\""));
    }

    private static bool IsCjk(Rune rune) => rune.Value is >= 0x3400 and <= 0x9FFF or
        >= 0x3040 and <= 0x30FF or >= 0xAC00 and <= 0xD7AF;
}
