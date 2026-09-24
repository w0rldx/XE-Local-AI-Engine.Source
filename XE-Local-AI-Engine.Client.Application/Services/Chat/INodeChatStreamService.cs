namespace XE_Local_AI_Engine.Client.Services.Chat;

public interface INodeChatStreamService
{
    IAsyncEnumerable<ChatStreamEvent> SendMessageAsync(NodeChatStreamRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     A send or regenerate the CALLER got wrong (empty content, a malformed seed), rejected before the stream starts.
/// </summary>
/// <remarks>
///     Typed so <c>LocalChatHub</c> forwards its sentence as a <c>HubException</c> without widening that to every
///     <see cref="ArgumentException" />, which it still is for every other caller.
/// </remarks>
public sealed class NodeChatInvalidRequestException : ArgumentException
{
    public NodeChatInvalidRequestException(string? message) : base(message)
    {
    }
}
