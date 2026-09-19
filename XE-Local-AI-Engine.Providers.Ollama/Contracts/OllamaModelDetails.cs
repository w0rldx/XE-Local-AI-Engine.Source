namespace XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     The per-model detail fields the node surfaces, flattened off OllamaSharp's show-model response so no consumer
///     needs the SDK type.
/// </summary>
public sealed class OllamaModelDetails
{
    /// <summary>Context length read out of the model info block, or null when it reports none.</summary>
    public required int? MaxContextTokens { get; init; }

    /// <summary>The capability tags the runtime reports (for example <c>tools</c>, <c>thinking</c>).</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>The model's prompt template, when the runtime reports one.</summary>
    public string? Template { get; init; }

    /// <summary>The model's baked-in system prompt, when the runtime reports one.</summary>
    public string? System { get; init; }

    /// <summary>The model's license text, when the runtime reports one.</summary>
    public string? License { get; init; }
}
