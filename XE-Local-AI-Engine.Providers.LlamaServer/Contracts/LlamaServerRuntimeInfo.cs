namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Live runtime facts about a running <c>(model, role)</c> llama-server process: currently the effective context
///     window the server actually loaded, read from its <c>/props</c> endpoint after readiness.
/// </summary>
/// <remarks>
///     Distinct from a model's ADVERTISED train context — it is the launched context window, or what the server
///     clamped it to — so the app's context budgeters and the UI meter size against the real window, not a guess.
/// </remarks>
public sealed class LlamaServerRuntimeInfo
{
    /// <summary>The per-slot context window (<c>default_generation_settings.n_ctx</c>) the running server reports.</summary>
    public required int EffectiveContextTokens { get; init; }
}
