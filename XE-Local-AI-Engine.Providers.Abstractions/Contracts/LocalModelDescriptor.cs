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
    ///     Whether the model reasons NATIVELY: its chat template bakes reasoning onto its own channel with no graded
    ///     <c>think:&lt;level&gt;</c> switch (the OpenAI harmony family, e.g. gpt-oss). Distinct from — and mutually
    ///     exclusive with — <see cref="IsReasoningCapable" />: a native model must NOT be routed into the graded path,
    ///     which would send a <c>think</c> field its template ignores and an <c>enable_thinking</c> kwarg it does not
    ///     have. Defaults to <see langword="false" />.
    /// </summary>
    public bool IsNativeReasoningCapable { get; init; }

    /// <summary>
    ///     Whether the model can accept image input (vision / multimodal). True only when a multimodal projector
    ///     (<c>mmproj</c>) companion is present locally for this model — the same file that gates the llama-server
    ///     <c>--mmproj</c> launch argument, so this flag never claims a vision capability the runtime cannot serve.
    ///     Defaults to <see langword="false" />.
    /// </summary>
    public bool IsMultimodalCapable { get; init; }

    /// <summary>
    ///     The Ollama-style capability tokens (for example <c>completion</c>, <c>tools</c>, <c>thinking</c>,
    ///     <c>native_reasoning</c>, <c>vision</c>) detected for the model. Empty when no capabilities could be determined.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}
