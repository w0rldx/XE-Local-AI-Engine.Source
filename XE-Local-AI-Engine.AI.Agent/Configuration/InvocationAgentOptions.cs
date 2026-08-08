namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for platform-driven invocation agents.
/// </summary>
public sealed class InvocationAgentOptions
{
    /// <summary>Configuration section containing platform invocation agent defaults.</summary>
    public const string Section = "Agent:Invocation";

    /// <summary>Prefix used when creating per-model invocation-agent names.</summary>
    [Required]
    public string AgentNamePrefix { get; set; } = "XeInvocation";
}
