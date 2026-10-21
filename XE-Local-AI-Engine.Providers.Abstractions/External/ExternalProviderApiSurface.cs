namespace XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The OpenAI-compatible API an external model is served on, declared per model by the operator.
/// </summary>
/// <remarks>
///     One gateway connection can serve models that answer only on one of the two surfaces, so this is a model
///     declaration, not a connection one. The values are explicit and persisted as numbers: never reorder them.
/// </remarks>
public enum ExternalProviderApiSurface
{
    /// <summary><c>POST {base}/chat/completions</c>; the default, and what a model stored without the field reads as.</summary>
    ChatCompletions = 0,

    /// <summary><c>POST {base}/responses</c>, sent statelessly (<c>store=false</c>, no previous-response id).</summary>
    Responses = 1
}
