namespace XE_Local_AI_Engine.Client.Hubs;

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     Represents local chat hub.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class LocalChatHub(
    INodeChatStreamService streamService,
    INodeChatRegenerationService regenerationService,
    IInvocationResumeRegistry resumeRegistry,
    IInvocationAttachmentTracker attachmentTracker) : Hub
{
    public IAsyncEnumerable<ChatStreamEvent> SendMessage(NodeChatStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TrackAttachment(streamService.SendMessageAsync(request, cancellationToken), cancellationToken);
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
        return TrackAttachment(regenerationService.RegenerateAsync(conversationId, originalMessageId, reasoningEffort, useLocalTools, useKnowledgeBase, selectedPath, cancellationToken),
            cancellationToken);
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
        return TrackAttachment(resumeRegistry.ResumeAsync(invocationId, cancellationToken), cancellationToken);
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
            : TrackAttachment(resumeRegistry.ResumeAsync(invocationId.Value, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Marks the invocation as WATCHED for as long as this stream is being consumed, so
    ///     <c>DetachedInvocationReaper</c> can end a run whose client went away and never came back.
    ///     <para>
    ///         This hub is the only attach site because all four stream entry points return from this one file. The
    ///         invocation id is latched off the FIRST event's <see cref="ChatStreamEvent.RequestId" /> rather than taken
    ///         from the arguments — <see cref="SendMessage" /> and <see cref="ResumeMessage" /> know it up front, but
    ///         <see cref="RegenerateMessage" /> and <see cref="ResumeConversation" /> mint it server-side, and latching
    ///         uniformly means an entry point added later is covered without touching this method.
    ///     </para>
    ///     <para>
    ///         The release is driven by <paramref name="cancellationToken" /> — the token SignalR cancels when the
    ///         client unsubscribes or disconnects — and NOT by the source enumerable completing. That distinction is the
    ///         whole feature: <c>NodeChatStreamService.SendMessageCoreAsync</c> deliberately awaits its pump and runner
    ///         tasks in the <c>finally</c> after its SSE loop exits, so the enumerable does not return until the entire
    ///         run is over. Releasing only from this method's <c>finally</c> therefore recorded the detach *after* the
    ///         run had already terminalized, leaving <c>DetachedInvocationReaper</c> with nothing to reap and the grace
    ///         silently dead. The <c>finally</c> stays as the normal-completion path; the handle is idempotent.
    ///     </para>
    /// </summary>
    private async IAsyncEnumerable<ChatStreamEvent> TrackAttachment(IAsyncEnumerable<ChatStreamEvent> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IDisposable? attachment = null;
        await using var registration = cancellationToken.Register(() => attachment?.Dispose()).ConfigureAwait(false);

        try
        {
            await foreach (var streamEvent in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (attachment is null)
                {
                    attachment = attachmentTracker.Attach(streamEvent.RequestId);

                    // Closes the one gap the callback cannot: a cancellation that fired while we were latching has
                    // already run its callback against a still-null field and will never run again.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        attachment.Dispose();
                    }
                }

                yield return streamEvent;
            }
        }
        finally
        {
            attachment?.Dispose();
        }
    }
}
