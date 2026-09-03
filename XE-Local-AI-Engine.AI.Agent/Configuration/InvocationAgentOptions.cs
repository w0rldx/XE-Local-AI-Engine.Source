namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class InvocationAgentOptions
{
    public const string Section = "Agent:Invocation";

    [Required]
    public string AgentNamePrefix { get; set; } = "XeInvocation";
}
