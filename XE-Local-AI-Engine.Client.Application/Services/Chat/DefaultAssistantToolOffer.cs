namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The Default Assistant's effective tool offer: the model it was resolved for and the offered tool names.
/// </summary>
public sealed class DefaultAssistantToolOffer
{
    public required string? ModelName { get; init; }

    public required IReadOnlyList<string> ToolNames { get; init; }
}
