namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Declared capability matrix of a cloud provider. Lives on the provider/factory rather than on the shared
///     <c>LocalModelDescriptor</c> contract because the local runtime never classifies a cloud model.
/// </summary>
public sealed class AgentModelCapabilities
{
    public required bool SupportsStreaming { get; init; }

    public required bool SupportsToolCalling { get; init; }

    public required bool SupportsParallelToolCalls { get; init; }

    public required bool SupportsStructuredOutput { get; init; }

    public required bool SupportsVision { get; init; }

    public required bool SupportsUsage { get; init; }

    public required bool SupportsServiceSideThreads { get; init; }
}
