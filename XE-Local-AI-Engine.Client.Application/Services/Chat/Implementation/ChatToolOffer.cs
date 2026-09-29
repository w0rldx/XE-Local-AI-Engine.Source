namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     The tool offer for one turn: whether tools are offered at all, and the allow-list that travels in the runtime
///     package (null whenever nothing is offered).
/// </summary>
internal sealed record ChatToolOffer
{
    public required bool OfferTools { get; init; }

    public required IReadOnlyList<AllowedToolDto>? AllowedTools { get; init; }
}
