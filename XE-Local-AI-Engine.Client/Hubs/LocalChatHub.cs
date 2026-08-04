namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Represents local chat hub.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class LocalChatHub(
    INodeChatStreamService streamService,
    INodeChatRegenerationService regenerationService,
    IInvocationResumeRegistry resumeRegistry) : Hub
{
    public IAsyncEnumerable<ChatStreamEvent> SendMessage(NodeChatStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return streamService.SendMessageAsync(request, cancellationToken);
    }

    /// <summary>
    ///     Regenerates an assistant turn as a SIBLING VARIANT and streams the run like a normal send:
    ///     assistant-queued/streaming/delta/completed. Mints the variant placeholder, drives it through the shared
    ///     runner/pump, and persists INTO that placeholder — never overwriting the original. Throws for an
    ///     Origin=Remote (view-only) conversation or an unknown conversation/message.
    /// </summary>
    public IAsyncEnumerable<ChatStreamEvent> RegenerateMessage(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort,
        bool useLocalTools,
        bool useKnowledgeBase,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        CancellationToken cancellationToken)
    {
        return regenerationService.RegenerateAsync(conversationId, originalMessageId, reasoningEffort, useLocalTools, useKnowledgeBase, selectedPath, cancellationToken);
    }

    /// <summary>
    ///     Re-attaches to a still-running invocation after the client reconnects with a NEW connection id. The
    ///     first event replays the content accumulated so far, then live deltas and the terminal event follow in
    ///     order. Throws when the invocation is unknown or already terminal — the client then re-fetches the
    ///     persisted conversation instead.
    /// </summary>
    public IAsyncEnumerable<ChatStreamEvent> ResumeMessage(Guid invocationId,
        CancellationToken cancellationToken)
    {
        return resumeRegistry.ResumeAsync(invocationId, cancellationToken);
    }

    /// <summary>
    ///     Re-attaches to whatever turn is still running in <paramref name="conversationId" />, for a client that has
    ///     just RELOADED and therefore holds no invocation id. <see cref="ResumeMessage" /> serves the reconnect case
    ///     (the page survived, so the id is still in memory); this serves the cold-load case, where the id is gone.
    ///     <para>
    ///         Without it a reload permanently loses an in-flight <c>ask_user</c> question or tool approval: the prompt
    ///         is transient live state that is deliberately never written into the conversation's persisted parts, so
    ///         re-fetching the conversation cannot bring it back, and the run stays parked until it times out.
    ///     </para>
    ///     <para>
    ///         Returns an empty stream when nothing is live, so the caller can invoke it unconditionally on open and
    ///         simply get nothing for an idle conversation.
    ///     </para>
    /// </summary>
    public IAsyncEnumerable<ChatStreamEvent> ResumeConversation(Guid conversationId,
        CancellationToken cancellationToken)
    {
        var invocationId = resumeRegistry.TryGetLiveInvocationIdForConversation(conversationId);
        return invocationId is null
            ? AsyncEnumerable.Empty<ChatStreamEvent>()
            : resumeRegistry.ResumeAsync(invocationId.Value, cancellationToken);
    }
}
