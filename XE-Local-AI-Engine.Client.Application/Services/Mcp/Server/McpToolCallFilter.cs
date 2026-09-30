namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
///     The inbound server's single call-tool filter: arguments are checked against the tool's advertised input schema
///     BEFORE binding, and a structured response carrying a top-level <c>failure_code</c> is flagged <c>isError</c>.
/// </summary>
/// <remarks>
///     An argument problem is a tool execution error (<c>invalid_arguments</c>), not a JSON-RPC protocol error: the MCP
///     spec routes input validation to <c>isError</c> so the calling model can correct itself, and a thrown exception —
///     <c>McpProtocolException</c> included — is logged by SDK 2.2.0 as an Error with its stack trace. An unknown argument
///     name is refused rather than dropped: a silently ignored <c>model_override</c> on <c>run_agent</c> was I-D2.
/// </remarks>
public static class McpToolCallFilter
{
    public const string InvalidArgumentsCode = "invalid_arguments";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return async (context, cancellationToken) =>
        {
            if (context.MatchedPrimitive is McpServerTool tool
                && FindArgumentProblem(tool.ProtocolTool.InputSchema, context.Params?.Arguments) is { } problem)
            {
                var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(McpToolCallFilter).FullName!)
                             ?? NullLogger.Instance;
                logger.LogWarning("Rejected inbound MCP tool call {ToolName}: {Problem}", tool.ProtocolTool.Name, problem);
                return McpToolResults.Failure(InvalidArgumentsCode, problem);
            }

            var result = await next(context, cancellationToken);
            if (result.Meta?.Remove(McpToolResults.FreeTextMetaKey) == true)
            {
                // Free model output (run_agent's answer) is the model's text, not a typed response: an answer that happens to be JSON
                // with a top-level failure_code is still a successful call. The marker is internal, so it is stripped before the wire.
                if (result.Meta.Count == 0)
                {
                    result.Meta = null;
                }

                return result;
            }

            if (result.IsError != true && CarriesFailureCode(result))
            {
                result.IsError = true;
            }

            return result;
        };
    }

    /// <summary>The first problem with <paramref name="arguments" /> against a tool's input schema, or <see langword="null" />.</summary>
    internal static string? FindArgumentProblem(JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object
            || !inputSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var required = inputSchema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray().Select(static name => name.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        arguments ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (name, value) in arguments)
        {
            if (!properties.TryGetProperty(name, out var propertySchema))
            {
                var accepted = string.Join(", ", properties.EnumerateObject().Select(static property => property.Name));
                return $"Unknown argument '{name}'. Accepted arguments: {accepted}.";
            }

            if (value.ValueKind == JsonValueKind.Null && !required.Contains(name))
            {
                continue;
            }

            if (AllowedTypes(propertySchema) is { Count: > 0 } allowed && !allowed.Any(type => Matches(type, value)))
            {
                return $"Argument '{name}' must be {string.Join(" or ", allowed.Where(static type => type != "null"))}.";
            }
        }

        foreach (var name in required)
        {
            if (!arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return $"Missing required argument '{name}'.";
            }
        }

        return null;
    }

    private static List<string> AllowedTypes(JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => [.. type.EnumerateArray().Select(static item => item.GetString()).OfType<string>()],
            _ => []
        };
    }

    private static bool Matches(string type, JsonElement value) =>
        type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind == JsonValueKind.Null,
            // A type this check does not model is left to the binder.
            _ => true
        };

    // The contract is "a top-level failure_code means the call failed" — one rule for agent and admin tools alike.
    private static bool CarriesFailureCode(CallToolResult result)
    {
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } structured)
        {
            return HasFailureCode(structured);
        }

        if (result.Content is not [TextContentBlock { Text: ['{', ..] text }])
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return HasFailureCode(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasFailureCode(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(McpToolResults.FailureCodeProperty, out var code)
        && code.ValueKind == JsonValueKind.String;
}
