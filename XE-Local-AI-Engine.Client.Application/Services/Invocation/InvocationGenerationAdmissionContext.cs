namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>Provider/runtime facts available after warm-up and before generation begins.</summary>
public sealed record InvocationGenerationAdmissionContext
{
    public required Guid InvocationId { get; init; }

    public required int RequestedContextTokens { get; init; }

    public required int? EffectiveContextTokens { get; init; }

    public required string ModelId { get; init; }

    public string? ProviderName { get; init; }
}
