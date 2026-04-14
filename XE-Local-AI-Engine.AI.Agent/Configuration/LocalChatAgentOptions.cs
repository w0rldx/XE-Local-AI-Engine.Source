namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

public sealed class LocalChatAgentOptions
{
    public const string Section = "Agent:LocalChat";

    [Required]
    public string AgentName { get; set; } = "XeLocalAgent";

    [Required]
    public string DefaultModel { get; set; } = "qwen3.5:9b";

    [Required]
    public string InstructionsResource { get; set; } = "XE_Local_AI_Engine.AI.Agent.Instructions.LocalChatDefault.txt";

    public bool EnableTools { get; set; } = true;
}
