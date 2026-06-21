namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

using System.Text.Json.Serialization;

public sealed record LocalModelDescriptor
{
    [JsonRequired]
    public required string ModelName { get; init; }

    [JsonRequired]
    public required string ProviderName { get; init; }

    [JsonRequired]
    public required bool IsAvailable { get; init; }

    [JsonRequired]
    public required long? SizeBytes { get; init; }

    [JsonRequired]
    public required DateTimeOffset? ModifiedAt { get; init; }

    [JsonRequired]
    public required int? MaxContextTokens { get; init; }

    /// <summary>
    ///     Whether the model's chat template advertises tool / function calling. Detected offline from the GGUF chat
    ///     template (no Ollama probe). Defaults to <see langword="false" /> — the safe default that offers no tools to a
    ///     model whose capability could not be determined.
    /// </summary>
    public bool IsToolCapable { get; init; }

    /// <summary>
    ///     Whether the model's chat template advertises a reasoning / thinking channel. Detected offline from the GGUF
    ///     chat template. Defaults to <see langword="false" /> so a non-reasoning model is never offered a graded effort.
    /// </summary>
    public bool IsReasoningCapable { get; init; }

    /// <summary>
    ///     The Ollama-style capability tokens (for example <c>completion</c>, <c>tools</c>, <c>thinking</c>) detected for
    ///     the model. Empty when no capabilities could be determined.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}
