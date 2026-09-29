namespace XE_Local_AI_Engine.Client.Services.Drafting.Implementation;

/// <summary>Structured-output envelope for an image-prompt draft. Bound-free for the same reason as <see cref="AgentDraftEnvelope" />.</summary>
internal sealed class ImagePromptDraftEnvelope
{
    public string? Prompt { get; init; }

    public string? NegativePrompt { get; init; }

    public string? Rationale { get; init; }

    public List<string>? Assumptions { get; init; }

    public double Confidence { get; init; }
}
