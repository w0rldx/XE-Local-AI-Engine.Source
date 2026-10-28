namespace XE_Local_AI_Engine.Providers.CodexOAuth;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>Capability values for Codex OAuth.</summary>
/// <remarks>
///     <c>SupportsToolCalling</c> is <see langword="true" /> for ALL Codex ids: the .NET serialization is proven by the
///     <c>CodexToolCallingSpikeWireTests</c> spike, and the stateless tool loop replays the encrypted reasoning item
///     through MEAI's verbatim <c>RawRepresentation is ResponseItem</c> path. <c>SupportsParallelToolCalls</c> stays
///     <see langword="false" />, single-call first. Both <c>ModelCapabilityResolver.ResolveAsync</c> and
///     <c>LocalModelsMapper</c>'s <c>IsToolCapable</c> tag read <see cref="V0" />, so one flag governs both surfaces.
/// </remarks>
public static class CodexProviderCapabilities
{
    public static AgentModelCapabilities V0 { get; } = new()
    {
        SupportsStreaming = true,
        SupportsToolCalling = true,
        SupportsParallelToolCalls = false,
        SupportsStructuredOutput = false,
        SupportsVision = false,
        SupportsUsage = true,
        SupportsServiceSideThreads = false
    };
}
