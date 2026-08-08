namespace XE_Local_AI_Engine.Providers.CodexOAuth;

/// <summary>
///     Declared capability matrix for the Codex OAuth provider. Lives on the provider/factory rather
///     than on the shared <c>LocalModelDescriptor</c> contract because Codex is a cloud provider.
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
///     Capability values for Codex OAuth. <c>SupportsToolCalling</c> is <see langword="true" /> for ALL
///     Codex ids: the .NET serialization is proven (spike <c>CodexToolCallingSpikeWireTests</c>) and the stateless
///     tool loop replays the encrypted reasoning item via MEAI's verbatim <c>RawRepresentation is ResponseItem</c>
///     path. <c>SupportsParallelToolCalls</c> stays <see langword="false" /> — single-call first. Every Codex tool
///     consumer (the chat capability gate in
///     <c>NodeChatStreamService.ResolveModelCapabilitiesAsync</c> and the <c>/models</c> <c>IsToolCapable</c> tag in
///     <c>LocalModelsMapper</c>) reads <see cref="V0" /> directly, so this single flag governs both surfaces.
/// </summary>
public static class CodexProviderCapabilities
{
    public static AgentModelCapabilities V0 { get; } = new()
    {
        SupportsStreaming = true,
        SupportsToolCalling = true,
        SupportsParallelToolCalls = false,
        SupportsStructuredOutput = false,
        SupportsVision = false,
        SupportsUsage = false,
        SupportsServiceSideThreads = false
    };
}
