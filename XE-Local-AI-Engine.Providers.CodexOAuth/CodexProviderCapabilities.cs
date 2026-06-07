namespace XE_Local_AI_Engine.Providers.CodexOAuth;

/// <summary>
/// Declared capability matrix for the Codex OAuth provider (plan §7). Lives on the provider/factory rather
/// than on the shared <c>LocalModelDescriptor</c> contract (Codex is a cloud provider, M8 dissolved).
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

/// <summary>
/// v0 capability values for Codex OAuth (plan §7). <c>SupportsToolCalling</c> stays <see langword="false"/>
/// until the Phase-2 real-backend tool round-trip passes (§4 / D5 / D6); the operator flips it on later.
/// </summary>
public static class CodexProviderCapabilities
{
    public static AgentModelCapabilities V0 { get; } = new()
    {
        SupportsStreaming = true,
        SupportsToolCalling = false,
        SupportsParallelToolCalls = false,
        SupportsStructuredOutput = false,
        SupportsVision = false,
        SupportsUsage = false,
        SupportsServiceSideThreads = false,
    };
}
