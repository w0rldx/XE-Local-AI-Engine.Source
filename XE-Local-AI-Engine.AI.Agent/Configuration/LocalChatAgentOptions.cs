namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for the node-local chat agent used by the React management UI.
/// </summary>
public sealed class LocalChatAgentOptions
{
    /// <summary>Configuration section containing local-chat agent defaults.</summary>
    public const string Section = "Agent:LocalChat";

    /// <summary>Display/name prefix assigned to the local chat agent.</summary>
    [Required]
    public string AgentName { get; set; } = "XeLocalAgent";

    /// <summary>Fallback local model id used when no request-specific model is supplied.</summary>
    [Required]
    public string DefaultModel { get; set; } = "qwen3.5:0.8b";

    /// <summary>Embedded instruction resource loaded as the local-chat system prompt.</summary>
    [Required]
    public string InstructionsResource { get; set; } = "XE_Local_AI_Engine.AI.Agent.Instructions.LocalChatDefault.txt";

    /// <summary>Whether the local chat offer list should include executable tools by default.</summary>
    public bool EnableTools { get; set; } = true;
}
