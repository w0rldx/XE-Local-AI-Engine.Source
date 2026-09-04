namespace XE_Local_AI_Engine.Client.Hubs;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.ProblemDetailModels.Enums;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
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
    IInvocationAttachmentTracker attachmentTracker,
    IOptions<SecurityOptions> securityOptions) : Hub
{
    public IAsyncEnumerable<ChatStreamEvent> SendMessage(NodeChatStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureMessageWithinSizeCap(request.Content);

        // RefuseUndeclaredWrites is a SERVER-set field: the development-workflow runtime puts it on the requests it
        // builds itself, and nothing arriving on this wire is entitled to arm a rule about a node it is not running.
        // Cleared rather than rejected, because a browser gains nothing by setting it — the most it could do is make
        // its own turn refuse — and a rejection would be a new failure mode on a field no client is meant to know
        // about. The copy is taken only when the field arrives set, so an ordinary send allocates nothing extra.
        if (request.RefuseUndeclaredWrites)
        {
            request = request with
            {
                RefuseUndeclaredWrites = false
            };
        }

        return TrackAttachment(streamService.SendMessageAsync(request, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     The message-size cap's ONLY enforcement point for a local send, and deliberately here rather than deeper:
    ///     <c>NodeChatStreamService.SendMessageCoreAsync</c> persists the user turn before anything downstream can
    ///     inspect its content, so a check any later leaves the oversized row in the conversation for every subsequent
    ///     turn to trip over.
    ///     <para>
    ///         Thrown as a <see cref="HubException" /> because that is the only exception type whose MESSAGE SignalR
    ///         forwards to the client at all (without detailed errors enabled, any other type reaches the browser as
    ///         the bare "An unexpected error occurred invoking 'SendMessage' on the server."). Note SignalR still
    ///         PREPENDS that generic sentence plus "HubException:" to the forwarded text — the chat page's
    ///         <c>errorMessage</c> helper strips that wrapper so the user reads this sentence first. The text names
    ///         both sizes and points at the attachment route, and carries no content, path or internal detail.
    ///     </para>
    /// </summary>
    private void EnsureMessageWithinSizeCap(string? content)
    {
        if (content is null)
        {
            return;
        }

        var maxSizeKb = securityOptions.Value.MaxMessageSizeKb;
        var sizeBytes = Encoding.UTF8.GetByteCount(content);
        if (sizeBytes <= maxSizeKb * 1024)
        {
            return;
        }

        // Rounded UP so a message reported as "N KB" is never at or below the stated limit.
        var sizeKb = (sizeBytes + 1023) / 1024;
        throw new HubException(string.Format(CultureInfo.InvariantCulture,
            "Your message is too large ({0} KB, limit {1} KB). Attach large documents as files instead.",
            sizeKb,
            maxSizeKb));
    }

    /// <summary>
    ///     Regenerates an assistant turn as a SIBLING VARIANT and streams the run like a normal send:
    ///     assistant-queued/streaming/delta/completed. Mints the variant placeholder, drives it through the shared
    ///     runner/pump, and persists INTO that placeholder — never overwriting the original. Throws for an
    ///     Origin=Remote (view-only) conversation or an unknown conversation/message.
    ///     <para>
    ///         <paramref name="samplingOptions" /> is the LAST wire argument on purpose: the client passes the same
    ///         developer-gated overrides a send carries, and appending keeps the existing positional order intact.
    ///     </para>
    /// </summary>
    public IAsyncEnumerable<ChatStreamEvent> RegenerateMessage(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort,
        bool useLocalTools,
        bool useKnowledgeBase,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        SamplingOptions? samplingOptions,
        CancellationToken cancellationToken)
    {
        return TrackAttachment(
            regenerationService.RegenerateAsync(conversationId, originalMessageId, reasoningEffort, useLocalTools, useKnowledgeBase, selectedPath, samplingOptions, cancellationToken),
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
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        IDisposable? attachment = null;
        await using var registration = cancellationToken.Register(() => attachment?.Dispose()).ConfigureAwait(false);

        try
        {
            await foreach (var streamEvent in TranslateDomainRejections(source, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    ///     Re-throws the stream services' TYPED caller-triggerable rejections as <see cref="HubException" />s — the
    ///     only exception type whose message SignalR forwards to the client (see
    ///     <see cref="EnsureMessageWithinSizeCap" />). Without this, a send or regenerate against an
    ///     <c>Origin=Remote</c> (view-only) conversation, a deleted conversation/message, or a correlation already
    ///     being generated all reach the browser as the bare "An unexpected error occurred invoking 'SendMessage' on
    ///     the server." The conversion is deliberately narrow — matched by exception TYPE, never by inspecting a
    ///     message string — because widening it to every fault would forward internal detail to the browser, which is
    ///     what SignalR's generic message exists to prevent.
    ///     <para>
    ///         Only the read-only rejection is PREFIXED, with the <see cref="NodeConflictProblemType.ReadOnlyConversation" />
    ///         name — the exact discriminator token the REST 409 carries as <c>ConflictProblemDetails.conflictType</c>,
    ///         so the SPA's <c>isNodeChatReadOnlyConflict</c> recognises both shapes off one constant. Referenced via
    ///         the enum, never a literal, so a rename there cannot silently desynchronise the two paths. The others
    ///         carry no token: no caller discriminates on them, and an unstripped token would be the first thing the
    ///         operator reads in the toast.
    ///     </para>
    ///     <para>
    ///         Composed into <see cref="TrackAttachment" /> rather than at the individual call sites, so all four stream
    ///         entry points are covered by one seam; it is a no-op for the resume paths, which never run the mutation
    ///         guard. The rejections are thrown LAZILY during enumeration (they live inside the services' async
    ///         iterators), so the conversion has to happen around <c>MoveNextAsync</c>; C# forbids <c>yield return</c>
    ///         inside a <c>try</c> that has a <c>catch</c>, hence the manual enumeration with the yield outside the try.
    ///     </para>
    /// </summary>
    private static async IAsyncEnumerable<ChatStreamEvent> TranslateDomainRejections(IAsyncEnumerable<ChatStreamEvent> source,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (NodeChatReadOnlyConversationException exception)
                {
                    throw new HubException($"{NodeConflictProblemType.ReadOnlyConversation}: {exception.Message}", exception);
                }
                catch (Exception exception) when (exception is NodeChatConversationNotFoundException
                                                      or NodeChatMessageNotFoundException
                                                      or NodeChatStreamAlreadyActiveException)
                {
                    throw new HubException(exception.Message, exception);
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }
}
