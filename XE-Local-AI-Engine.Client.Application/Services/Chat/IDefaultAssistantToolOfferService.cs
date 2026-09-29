namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Reads the tool offer a chat turn hands the seeded Default Assistant, so the agent form shows the server's list
///     instead of guessing it.
/// </summary>
public interface IDefaultAssistantToolOfferService
{
    /// <summary>
    ///     The Default Assistant's effective tool offer on <paramref name="modelName" />, or on the node's local default
    ///     chat model when it is blank.
    /// </summary>
    /// <remarks>
    ///     Read-only: it runs the send path's resolution (capabilities, trust boundary, node switches, approval policy)
    ///     without loading a model. An Ollama-routed id may warm the classification cache, as the model list does.
    /// </remarks>
    Task<DefaultAssistantToolOffer> GetAsync(string? modelName, CancellationToken cancellationToken = default);
}

/// <summary>
///     The Default Assistant's effective tool offer: the model it was resolved for and the offered tool names.
/// </summary>
public sealed class DefaultAssistantToolOffer
{
    public required string? ModelName { get; init; }

    public required IReadOnlyList<string> ToolNames { get; init; }
}
