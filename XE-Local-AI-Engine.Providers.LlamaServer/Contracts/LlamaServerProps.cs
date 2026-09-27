namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.Text.Json;

/// <summary>The fields the node reads from a running llama-server's <c>/props</c> document.</summary>
/// <remarks>
///     The ONE parser for that document: the health probe reads the effective context from it after readiness and the
///     training smoke gate reads the chat template, so the two can never disagree about what the server reported.
/// </remarks>
public sealed class LlamaServerProps
{
    /// <summary>
    ///     The per-slot context window (<c>default_generation_settings.n_ctx</c>), or <see langword="null" /> when it is
    ///     absent, not an integer, or not positive.
    /// </summary>
    public required int? EffectiveContextTokens { get; init; }

    /// <summary>Whether the server resolved a non-blank <c>chat_template</c> string.</summary>
    public required bool HasChatTemplate { get; init; }

    /// <summary>Parses a <c>/props</c> body.</summary>
    /// <exception cref="JsonException">The body is not JSON.</exception>
    /// <exception cref="InvalidOperationException">The body is JSON but not an object.</exception>
    public static async Task<LlamaServerProps> ReadAsync(Stream body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;

        int? contextTokens = root.TryGetProperty("default_generation_settings", out var settings)
                             && settings.ValueKind == JsonValueKind.Object
                             && settings.TryGetProperty("n_ctx", out var nCtx)
                             && nCtx.ValueKind == JsonValueKind.Number
                             && nCtx.TryGetInt32(out var parsed)
                             && parsed > 0
            ? parsed
            : null;

        return new LlamaServerProps
        {
            EffectiveContextTokens = contextTokens,
            HasChatTemplate = root.TryGetProperty("chat_template", out var template)
                              && template.ValueKind == JsonValueKind.String
                              && !string.IsNullOrWhiteSpace(template.GetString())
        };
    }
}
