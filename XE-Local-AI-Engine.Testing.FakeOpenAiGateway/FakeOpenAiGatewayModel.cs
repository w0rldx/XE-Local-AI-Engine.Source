namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>One entry of the fake gateway's model list.</summary>
public sealed record FakeOpenAiGatewayModel
{
    public required string Id { get; init; }

    /// <summary>The <c>context_length</c> extra a gateway reports beside the OpenAI fields.</summary>
    public required int ContextLength { get; init; }

    /// <summary>The <c>max_output_tokens</c> extra.</summary>
    public int MaxOutputTokens { get; init; } = 4096;

    public string OwnedBy { get; init; } = "fake-gateway";

    /// <summary>Optional free-form <c>metadata</c> object; omitted from the wire when null.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
