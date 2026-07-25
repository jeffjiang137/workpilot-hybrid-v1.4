using System.Runtime.CompilerServices;
using System.Text;

namespace WorkPilot.Services;

public static class SseParser
{
    public static async IAsyncEnumerable<string> ReadDataAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken, int maxEventChars = 4 * 1024 * 1024)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var data = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    if (data[^1] == '\n') data.Length--;
                    yield return data.ToString();
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                if (data.Length + value.Length + 1 > maxEventChars)
                    throw new InvalidDataException($"SSE event 超过 {maxEventChars} 字符上限");
                data.Append(value).Append('\n');
            }
        }
        if (data.Length > 0)
        {
            if (data[^1] == '\n') data.Length--;
            yield return data.ToString();
        }
    }
}
