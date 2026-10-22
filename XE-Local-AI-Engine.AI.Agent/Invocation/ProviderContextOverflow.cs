namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using System.Text.Json;

/// <summary>
///     A provider's HTTP 400 rejecting a request for its context size, read from the response body: llama-server's
///     <c>exceed_context_size_error</c> or an OpenAI-compatible <c>context_length_exceeded</c>.
/// </summary>
/// <remarks>
///     llama-server adds <c>n_prompt_tokens</c> and <c>n_ctx</c> (the per-slot window) beside the type; gateways carry no
///     structured counts, so both stay null there. Numbers only, never the body's text.
/// </remarks>
public sealed record ProviderContextOverflow
{
    /// <summary>Fixed, path-free message surfaced for a provider context-size rejection.</summary>
    public const string Message = "The request exceeded the model's context window. Shorten the input or let compaction run.";

    /// <summary>The prompt tokens the provider counted for the rejected request, when it reported them.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>The provider's context window for the request, when it reported it.</summary>
    public int? ContextTokens { get; init; }

    /// <summary>The overflow an HTTP 400 <paramref name="error" /> reports, or null for any other status or body.</summary>
    public static ProviderContextOverflow? From(ProviderErrorBody? error)
    {
        return error is { Status: 400 } ? Parse(error.Body) : null;
    }

    /// <summary>The overflow <paramref name="body" /> reports, or null when it is not a context-size rejection or not JSON.</summary>
    /// <remarks>The counts are read beside the type, or at the top level when a proxy flattened the error object.</remarks>
    public static ProviderContextOverflow? Parse(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !(HasString(error, "type", "exceed_context_size_error") || HasString(error, "code", "context_length_exceeded")))
            {
                return null;
            }

            var root = document.RootElement;
            return new ProviderContextOverflow
            {
                PromptTokens = ReadPositiveInt(error, "n_prompt_tokens") ?? ReadPositiveInt(root, "n_prompt_tokens"),
                ContextTokens = ReadPositiveInt(error, "n_ctx") ?? ReadPositiveInt(root, "n_ctx")
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasString(JsonElement error, string name, string expected)
    {
        return error.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String
               && string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static int? ReadPositiveInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;
    }
}
