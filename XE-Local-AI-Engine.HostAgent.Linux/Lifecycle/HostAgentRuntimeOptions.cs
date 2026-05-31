namespace XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;

using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

/// <summary>
///     Configuration options for host agent runtime behavior.
/// </summary>
public sealed record HostAgentRuntimeOptions
{
    public const string SectionName = "HostAgent:Runtime";

    public string RuntimeNetwork { get; init; } = "xe-engine-net";

    public HostAgentManifest? Manifest { get; init; }
}
