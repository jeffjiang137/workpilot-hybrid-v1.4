using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Application.Security;

/// <summary>
/// Streams the security audit log to JSONL (SEC-106). One entry per line, ordered by sequence.
/// Bound to <paramref name="maxRows"/> (default 100,000) so an export never exhausts memory; callers
/// needing more should page. Output contains only safe, display-name-free fields — never secrets.
/// </summary>
public sealed class AuditExporter
{
    private readonly IAuditLogStore _store;

    public AuditExporter(IAuditLogStore store) => _store = store;

    public async Task<long> ExportJsonLAsync(Stream output, CancellationToken ct, int maxRows = 100_000)
    {
        var entries = await _store.GetAllAsync(ct);
        var ordered = entries.OrderBy(e => e.Sequence, Comparer<long>.Default);

        var sb = new StringBuilder(512);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true);

        var n = 0;
        foreach (var e in ordered)
        {
            if (n >= maxRows) break;
            sb.Clear();
            sb.Append('{');
            AppendField(sb, "sequence", e.Sequence.ToString(CultureInfo.InvariantCulture));
            AppendField(sb, "occurred_at_utc", e.OccurredAtUtc.ToString("O"));
            AppendField(sb, "category", ((int)e.Category).ToString(CultureInfo.InvariantCulture));
            AppendField(sb, "action", e.Action);
            AppendField(sb, "actor", e.Actor);
            AppendField(sb, "subject_json", e.SubjectJson, rawJson: true);
            AppendField(sb, "decision_trace_json", e.DecisionTraceJson, rawJson: true);
            AppendField(sb, "safe_detail_json", e.SafeDetailJson, rawJson: true);
            AppendField(sb, "prev_hmac", e.PrevHmac);
            AppendField(sb, "hmac", e.Hmac);
            AppendField(sb, "created_at_utc", e.CreatedAtUtc.ToString("O"));
            sb.Append('}');
            await writer.WriteLineAsync(sb.ToString());
            n++;
        }

        await writer.FlushAsync(ct);
        return n;
    }

    private static void AppendField(StringBuilder sb, string name, string value, bool rawJson = false)
    {
        sb.Append('"').Append(name).Append("\":");
        if (rawJson)
            sb.Append(string.IsNullOrEmpty(value) ? "null" : value);
        else
            sb.Append('"').Append(Escape(value)).Append('"');
        sb.Append(',');
    }

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
