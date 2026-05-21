namespace XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;

using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

public sealed record HostAgentRuntimeOptions
{
    public const string SectionName = "HostAgent:Runtime";

    public string RuntimeNetwork { get; init; } = "xe-engine-net";

    public HostAgentManifest? Manifest { get; init; }
}
