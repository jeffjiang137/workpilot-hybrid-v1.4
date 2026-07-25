using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class OpenAiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<ModelTurn> StreamChatAsync(string endpoint, string apiKey, string model,
        IReadOnlyList<ModelMessage> messages, bool toolsEnabled, bool assetSearchEnabled,
        IReadOnlyList<ModelToolDefinition> externalTools, Func<string, Task> onDelta,
        CancellationToken cancellationToken)
    {
        var hasTools = toolsEnabled || externalTools.Count > 0;
        var requestBody = new
        {
            model,
            messages,
            tools = hasTools ? ToolDefinitions.Get(toolsEnabled, assetSearchEnabled, externalTools) : null,
            tool_choice = hasTools ? "auto" : null,
            stream = true
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            endpoint.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"模型服务返回 {(int)response.StatusCode}: {Limit(body, 800)}");
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return await ReadJsonResponseAsync(response, cancellationToken);

        var text = new StringBuilder();
        var calls = new Dictionary<int, ModelToolCall>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var data in SseParser.ReadDataAsync(stream, cancellationToken))
        {
            if (data == "[DONE]") break;
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new HttpRequestException("模型流返回错误：" + Limit(error.ToString(), 800));
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("delta", out var delta)) continue;
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var value = content.GetString() ?? "";
                text.Append(value);
                await onDelta(value);
            }
            if (delta.TryGetProperty("tool_calls", out var toolCalls)) MergeToolCalls(toolCalls, calls);
        }
        return new ModelTurn(text.ToString(), calls.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList());
    }

    private static void MergeToolCalls(JsonElement values, IDictionary<int, ModelToolCall> calls)
    {
        foreach (var value in values.EnumerateArray())
        {
            var index = value.GetProperty("index").GetInt32();
            if (!calls.TryGetValue(index, out var call)) calls[index] = call = new ModelToolCall();
            if (value.TryGetProperty("id", out var id)) call.Id = id.GetString() ?? call.Id;
            if (!value.TryGetProperty("function", out var function)) continue;
            if (function.TryGetProperty("name", out var name)) call.Function.Name += name.GetString();
            if (function.TryGetProperty("arguments", out var arguments)) call.Function.Arguments += arguments.GetString();
        }
    }

    private static async Task<ModelTurn> ReadJsonResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentValue) ? contentValue.GetString() ?? "" : "";
        var calls = message.TryGetProperty("tool_calls", out var callValues)
            ? JsonSerializer.Deserialize<List<ModelToolCall>>(callValues.GetRawText(), JsonOptions) ?? [] : [];
        return new ModelTurn(content, calls);
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    public void Dispose() => _http.Dispose();

    private static class ToolDefinitions
    {
        private static readonly object[] FileTools =
        [
            Tool("list_files", "列出当前项目工作区中的文件和目录", new
            {
                type = "object", properties = new { path = new { type = "string", description = "相对路径，根目录为空字符串" } },
                required = Array.Empty<string>(), additionalProperties = false
            }),
            Tool("read_text_file", "读取工作区内不超过 512 KiB 的 UTF-8 文本文件", new
            {
                type = "object", properties = new { path = new { type = "string" } },
                required = new[] { "path" }, additionalProperties = false
            }),
            Tool("write_text_file", "原子写入工作区内不超过 1 MiB 的 UTF-8 文本文件", new
            {
                type = "object", properties = new
                {
                    path = new { type = "string" }, content = new { type = "string" },
                    expected_sha256 = new { type = new[] { "string", "null" }, description = "读取时返回的哈希；新文件可省略" }
                },
                required = new[] { "path", "content" }, additionalProperties = false
            })
        ];

        private static readonly object AssetSearch = Tool("search_assets",
            "在当前项目的本地文本资产索引中搜索，只返回不可信引用数据", new
            {
                type = "object", properties = new
                {
                    query = new { type = "string", minLength = 1, maxLength = 200 },
                    max_results = new { type = "integer", minimum = 1, maximum = 8 }
                },
                required = new[] { "query" }, additionalProperties = false
            });

        public static object[] Get(bool fileToolsEnabled, bool assetSearchEnabled,
            IReadOnlyList<ModelToolDefinition> externalTools)
        {
            var result = new List<object>();
            if (fileToolsEnabled) result.AddRange(FileTools);
            if (fileToolsEnabled && assetSearchEnabled) result.Add(AssetSearch);
            foreach (var tool in externalTools)
            {
                using var schema = JsonDocument.Parse(tool.ParametersJson);
                result.Add(Tool(tool.Name, tool.Description, schema.RootElement.Clone()));
            }
            return [.. result];
        }

        private static object Tool(string name, string description, object parameters) =>
            new { type = "function", function = new { name, description, parameters } };
    }
}
