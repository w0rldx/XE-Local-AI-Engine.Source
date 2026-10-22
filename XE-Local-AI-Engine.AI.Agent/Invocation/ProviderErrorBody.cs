namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>A provider status failure's HTTP status and raw response body.</summary>
public sealed record ProviderErrorBody
{
    public required int Status { get; init; }

    public required ReadOnlyMemory<byte> Body { get; init; }
}
