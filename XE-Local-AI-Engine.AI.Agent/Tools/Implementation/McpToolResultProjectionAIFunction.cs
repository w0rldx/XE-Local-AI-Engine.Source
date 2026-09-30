namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     Projects an MCP tool result into the text the model reads: joined text blocks, one <c>structuredContent</c> copy
///     unless a text block mirrors it, an <c>isError</c> failure prefix, and a placeholder per image or audio block.
/// </summary>
/// <remarks>
///     <c>McpClientTool</c> returns <c>AIContent</c>/<c>AIContent[]</c> on the plain path and a <see cref="JsonElement" /> of
///     the whole <c>CallToolResult</c> otherwise; the provider boundary serializes either as escaped JSON (images as base64
///     text). The binary blocks ride <see cref="ToolResultMedia" /> so the chat can still persist and render them. Sits
///     inside the result budget, so the budget measures the projected text.
/// </remarks>
internal sealed class McpToolResultProjectionAIFunction : DelegatingAIFunction
{
    public McpToolResultProjectionAIFunction(AIFunction inner)
        : base(inner)
    {
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        return Project(await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false));
    }

    internal static object? Project(object? result) =>
        result switch
        {
            AIContent content => Build([content], structured: null, isError: false),
            IEnumerable<AIContent> contents => Build([.. contents], structured: null, isError: false),
            JsonElement { ValueKind: JsonValueKind.Object } callToolResult => ProjectCallToolResult(callToolResult),
            _ => result
        };

    private static string ProjectCallToolResult(JsonElement result)
    {
        var blocks = new List<AIContent>();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                blocks.Add(ToContent(block));
            }
        }

        var structured = result.TryGetProperty("structuredContent", out var structuredContent) && structuredContent.ValueKind != JsonValueKind.Null
            ? structuredContent.GetRawText()
            : null;
        var isError = result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        return Build(blocks, structured, isError);
    }

    // The wire block shapes of the MCP spec: text, image and audio (base64 + mimeType), an embedded text resource.
    // Anything else keeps its raw JSON, so nothing a server sends is silently dropped.
    private static AIContent ToContent(JsonElement block)
    {
        var type = block.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        switch (type)
        {
            case "text" when block.TryGetProperty("text", out var text):
                return new TextContent(text.GetString());
            case "image" or "audio" when block.TryGetProperty("data", out var data) && block.TryGetProperty("mimeType", out var mimeType):
                try
                {
                    return new DataContent(Convert.FromBase64String(data.GetString() ?? string.Empty), mimeType.GetString() ?? "application/octet-stream");
                }
                catch (FormatException)
                {
                    return new TextContent($"[{type}: invalid base64 data]");
                }
            case "resource" when block.TryGetProperty("resource", out var resource) && resource.TryGetProperty("text", out var resourceText):
                return new TextContent(resourceText.GetString());
            default:
                return new TextContent(block.GetRawText());
        }
    }

    private static string Build(IReadOnlyList<AIContent> blocks, string? structured, bool isError)
    {
        var text = new StringBuilder();
        var media = new List<DataContent>();
        foreach (var block in blocks)
        {
            if (text.Length > 0)
            {
                _ = text.Append('\n');
            }

            switch (block)
            {
                case TextContent textContent:
                    _ = text.Append(textContent.Text);
                    break;
                case DataContent data:
                    media.Add(data);
                    _ = text.Append(Placeholder(data));
                    break;
                default:
                    _ = text.Append(block.ToString());
                    break;
            }
        }

        // FastMCP and most SDKs mirror structuredContent into a text block; only a result that did NOT gets the copy.
        if (structured is not null && !ContainsJson(text.ToString(), structured))
        {
            _ = text.Append(text.Length > 0 ? "\n" : string.Empty).Append(structured);
        }

        var projected = isError ? ToolFailureText.Format(ToolFailureText.ToolReportedCode, text.ToString()) : text.ToString();
        ToolResultMedia.Attach(projected, media);
        return projected;
    }

    // "[image image/png, 34 KB]": what the model can know about a block it cannot see.
    private static string Placeholder(DataContent data)
    {
        var topLevelType = data.MediaType.Split('/')[0];
        var kind = topLevelType is "image" or "audio" ? topLevelType : "file";
        var kilobytes = Math.Max(1, (data.Data.Length + 1023) / 1024);
        return string.Create(CultureInfo.InvariantCulture, $"[{kind} {data.MediaType}, {kilobytes} KB]");
    }

    // Verbatim, the whole text is the same JSON value spelled differently (a pretty-printed mirror), or the structured
    // value is FastMCP's {"result": <text>} wrapper around a non-object return (live re-run: a 100k string went out twice).
    private static bool ContainsJson(string text, string json)
    {
        if (text.Contains(json, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            using var expected = JsonDocument.Parse(json);
            var root = expected.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var wrapped) && CountProperties(root) == 1)
            {
                if (wrapped.ValueKind == JsonValueKind.String)
                {
                    return string.Equals(wrapped.GetString(), text, StringComparison.Ordinal);
                }

                root = wrapped;
            }

            using var actual = JsonDocument.Parse(text);
            return JsonElement.DeepEquals(root, actual.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject())
        {
            count++;
        }

        return count;
    }
}
