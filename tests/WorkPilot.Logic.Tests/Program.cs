using System.Text;
using WorkPilot.Services;
using WorkPilot.Models;

static async Task<IReadOnlyList<string>> ParseAsync(string input)
{
    await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
    var result = new List<string>();
    await foreach (var value in SseParser.ReadDataAsync(stream, CancellationToken.None)) result.Add(value);
    return result;
}

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected '{expected}', got '{actual}'");
}

var crlf = await ParseAsync("event: message\r\ndata: {\"a\":1}\r\n\r\ndata: [DONE]\r\n\r\n");
Equal(2, crlf.Count, "CRLF event count");
Equal("{\"a\":1}", crlf[0], "CRLF first event");
Equal("[DONE]", crlf[1], "CRLF done event");

var multiline = await ParseAsync("data: first\ndata: second\n\n");
Equal("first\nsecond", multiline.Single(), "multiline data");

var finalWithoutBlank = await ParseAsync("data: tail");
Equal("tail", finalWithoutBlank.Single(), "final event without blank line");

var expanded = SearchTextNormalizer.ExpandForFts("产品设计 App-File", true);
if (!expanded.Contains("产 品 设 计", StringComparison.Ordinal) || !expanded.Contains("产品", StringComparison.Ordinal))
    throw new InvalidOperationException("CJK unigram/bigram expansion failed");
var match = SearchTextNormalizer.BuildMatchQuery("hello \"world\"", out var simplified);
Equal(false, simplified, "short query is not simplified");
if (!match.Contains("\"hello\"", StringComparison.Ordinal)) throw new InvalidOperationException("FTS query quoting failed");

var source = string.Join("\n", Enumerable.Range(0, 900).Select(x => $"第{x}行 alpha_beta value"));
var chunks = TextChunker.Chunk(source, "sample.md", "docs/sample.md");
if (chunks.Count < 2 || chunks.Any(x => x.TokenEstimate > WorkPilot.Models.IndexPolicyV13.MaxChunkTokens))
    throw new InvalidOperationException("bounded chunking failed");
if (chunks.Zip(chunks.Skip(1)).Any(pair => pair.First.EndOffset <= pair.Second.StartOffset))
    throw new InvalidOperationException("chunk overlap missing");

TaskRules.Validate("实现资产搜索", "", "todo", "normal", "2026-07-20");
TaskRules.ValidateTransition("todo", "in_progress");
var invalidTransitionRejected = false;
try { TaskRules.ValidateTransition("done", "blocked"); }
catch (WorkPilot.Models.ValidationError) { invalidTransitionRejected = true; }
Equal(true, invalidTransitionRejected, "invalid task transition rejected");

var candidates = new[]
{
    new SkillCandidate("acme.prd", "v1", "1.0.0", "产品 PRD", "整理产品需求文档",
        ["写PRD", "需求文档"], ["prd", "product"], 0, false, []),
    new SkillCandidate("acme.code", "v2", "1.0.0", "代码审查", "审查代码",
        ["代码审查"], ["code"], 1, false, [])
};
var selectedSkills = SkillSelector.Select("请帮我写PRD并整理需求", candidates, new HashSet<string>());
Equal("acme.prd", selectedSkills.Single().SkillId, "deterministic skill selection");
if (!selectedSkills.Single().Matches.Any(x => x.Contains("别名", StringComparison.Ordinal)))
    throw new InvalidOperationException("skill selection evidence missing");

var validArguments = JsonSchemaGuard.ValidateObject(
    "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"maxLength\":5}},\"required\":[\"name\"],\"additionalProperties\":false}",
    "{\"name\":\"test\"}");
Equal("test", validArguments.GetProperty("name").GetString(), "schema guard valid value");
var unknownRejected = false;
try { JsonSchemaGuard.ValidateObject("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}", "{\"secret\":1}"); }
catch (ArgumentException) { unknownRejected = true; }
Equal(true, unknownRejected, "schema guard unknown property");

Equal(true, McpEndpointPolicy.IsPrivateOrReserved(System.Net.IPAddress.Parse("127.0.0.1")), "loopback blocked");
Equal(true, McpEndpointPolicy.IsPrivateOrReserved(System.Net.IPAddress.Parse("169.254.169.254")), "metadata address blocked");
Equal(true, McpEndpointPolicy.IsPrivateOrReserved(System.Net.IPAddress.Parse("10.1.2.3")), "private address blocked");
Equal(true, McpEndpointPolicy.IsPrivateOrReserved(System.Net.IPAddress.Parse("192.0.2.1")), "documentation address blocked");
Equal(true, McpEndpointPolicy.IsPrivateOrReserved(System.Net.IPAddress.Parse("198.18.0.1")), "benchmark address blocked");
Equal(false, McpEndpointPolicy.IsPrivateOrReserved(System.Net.IPAddress.Parse("8.8.8.8")), "public address accepted");

Console.WriteLine("WorkPilot.Logic.Tests passed");
