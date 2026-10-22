namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>The declared total context window of a recognised cloud model (a stored Azure Foundry deployment or a sourced Codex id).</summary>
public sealed record CloudModelContextWindow
{
    /// <summary>The window in tokens, or <see langword="null" /> for a deployment the operator declared no window for.</summary>
    public int? ContextTokens { get; init; }
}
