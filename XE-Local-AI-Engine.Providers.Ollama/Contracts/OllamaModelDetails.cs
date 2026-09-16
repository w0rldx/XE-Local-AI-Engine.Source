namespace XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     The per-model detail fields the node surfaces, flattened off OllamaSharp's show-model response so no consumer
///     needs the SDK type.
/// </summary>
/// <param name="MaxContextTokens">Context length read out of the model info block, or null when it reports none.</param>
/// <param name="Capabilities">The capability tags the runtime reports (for example <c>tools</c>, <c>thinking</c>).</param>
/// <param name="Template">The model's prompt template, when the runtime reports one.</param>
/// <param name="System">The model's baked-in system prompt, when the runtime reports one.</param>
/// <param name="License">The model's license text, when the runtime reports one.</param>
public sealed record OllamaModelDetails(
    int? MaxContextTokens,
    IReadOnlyList<string> Capabilities,
    string? Template = null,
    string? System = null,
    string? License = null);
