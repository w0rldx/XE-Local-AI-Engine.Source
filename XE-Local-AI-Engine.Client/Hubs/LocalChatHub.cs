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
public sealed class LocalChatHub : Hub
{
    private readonly INodeChatStreamService _streamService;
    private readonly INodeChatRegenerationService _regenerationService;
    private readonly IInvocationResumeRegistry _resumeRegistry;
    private readonly IInvocationAttachmentTracker _attachmentTracker;
    private readonly IOptions<SecurityOptions> _securityOptions;

    public LocalChatHub(INodeChatStreamService streamService,
        INodeChatRegenerationService regenerationService,
        IInvocationResumeRegistry resumeRegistry,
        IInvocationAttachmentTracker attachmentTracker,
        IOptions<SecurityOptions> securityOptions)
    {
        _streamService = streamService;
        _regenerationService = regenerationService;
        _resumeRegistry = resumeRegistry;
        _attachmentTracker = attachmentTracker;
        _securityOptions = securityOptions;
    }

    public IAsyncEnumerable<ChatStreamEvent> SendMessage(NodeChatStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureMessageWithinSizeCap(request.Content);

        // RefuseUndeclaredWrites is a SERVER-set field the development-workflow runtime puts on its own requests; nothing on
        // this wire may arm it. Cleared, not rejected: a browser gains nothing by setting it, and rejecting would add a failure mode on a field no client knows about. Copied only when set.
        if (request.RefuseUndeclaredWrites)
        {
            request = request with
            {
                RefuseUndeclaredWrites = false
            };
        }

        return TrackAttachment(RejectInvalidRequest(() => _streamService.SendMessageAsync(request, cancellationToken)), cancellationToken);
    }

    /// <summary>
    ///     The message-size cap's ONLY enforcement point for a local send.
    /// </summary>
    /// <remarks>
    ///     Here rather than deeper because <c>NodeChatStreamService.SendMessageCoreAsync</c> persists the user turn
    ///     before anything downstream can inspect its content, so a later check leaves the oversized row in the
    ///     conversation for every subsequent turn to trip over. Thrown as a <see cref="HubException" /> so the message
    ///     reaches the browser at all; the text names both sizes and points at the attachment route, and carries no
    ///     content, path or internal detail. See docs/wiki/09-api-and-hubs.md ("Chat hub: attachment and refusals").
    /// </remarks>
    private void EnsureMessageWithinSizeCap(string? content)
    {
        if (content is null)
        {
            return;
        }

        var maxSizeKb = _securityOptions.Value.MaxMessageSizeKb;
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
    ///     assistant-queued/streaming/delta/completed.
    /// </summary>
    /// <remarks>
    ///     Mints the variant placeholder, drives it through the shared runner/pump, and persists INTO that placeholder —
    ///     never overwriting the original. Throws for an Origin=Remote (view-only) conversation or an unknown
    ///     conversation/message. <paramref name="samplingOptions" /> is the LAST wire argument on purpose: the client
    ///     passes the same developer-gated overrides a send carries, and appending keeps the positional order intact.
    /// </remarks>
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
            RejectInvalidRequest(() =>
                _regenerationService.RegenerateAsync(conversationId, originalMessageId, reasoningEffort, useLocalTools, useKnowledgeBase, selectedPath, samplingOptions, cancellationToken)),
            cancellationToken);
    }

    /// <summary>
    ///     Re-attaches to a still-running invocation after the client reconnects with a NEW connection id.
    /// </summary>
    /// <remarks>
    ///     The first event replays the content accumulated so far, then live deltas and the terminal event follow in
    ///     order. Throws when the invocation is unknown or already terminal — the client then re-fetches the persisted
    ///     conversation instead.
    /// </remarks>
    public IAsyncEnumerable<ChatStreamEvent> ResumeMessage(Guid invocationId,
        CancellationToken cancellationToken)
    {
        return TrackAttachment(_resumeRegistry.ResumeAsync(invocationId, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Re-attaches to whatever turn is still running in <paramref name="conversationId" />, for a client that has
    ///     just RELOADED and therefore holds no invocation id.
    /// </summary>
    /// <remarks>
    ///     <see cref="ResumeMessage" /> serves the reconnect case, where the page survived and the id is still in
    ///     memory; this serves the cold-load case. Without it a reload permanently loses an in-flight <c>ask_user</c>
    ///     question or tool approval: the prompt is transient live state deliberately never written into the
    ///     conversation's persisted parts, so re-fetching cannot bring it back and the run stays parked until it times
    ///     out. Returns an empty stream when nothing is live, so the caller can invoke it unconditionally on open.
    /// </remarks>
    public IAsyncEnumerable<ChatStreamEvent> ResumeConversation(Guid conversationId,
        CancellationToken cancellationToken)
    {
        var invocationId = _resumeRegistry.TryGetLiveInvocationIdForConversation(conversationId);
        return invocationId is null
            ? AsyncEnumerable.Empty<ChatStreamEvent>()
            : TrackAttachment(_resumeRegistry.ResumeAsync(invocationId.Value, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Marks the invocation as WATCHED for as long as this stream is being consumed, so
    ///     <c>DetachedInvocationReaper</c> can end a run whose client went away and never came back.
    /// </summary>
    /// <remarks>
    ///     The invocation id is latched off the FIRST event's <see cref="ChatStreamEvent.RequestId" /> rather than taken
    ///     from the arguments, so an entry point added later is covered without touching this method. The release is
    ///     driven by <paramref name="cancellationToken" /> — the token SignalR cancels when the client unsubscribes or
    ///     disconnects — and NOT by the source enumerable completing, which does not return until the whole run is over
    ///     and would record the detach too late to reap. See docs/wiki/09-api-and-hubs.md ("Chat hub: attachment and refusals").
    /// </remarks>
    private async IAsyncEnumerable<ChatStreamEvent> TrackAttachment(IAsyncEnumerable<ChatStreamEvent> source,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        IDisposable? attachment = null;
        await using var registration = cancellationToken.Register(() => attachment?.Dispose());

        try
        {
            await foreach (var streamEvent in TranslateDomainRejections(source, cancellationToken).WithCancellation(cancellationToken))
            {
                if (attachment is null)
                {
                    attachment = _attachmentTracker.Attach(streamEvent.RequestId);

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
    ///     Re-throws a send/regenerate the caller got wrong as a <see cref="HubException" />. These are validated EAGERLY,
    ///     before the stream exists, so <see cref="TranslateDomainRejections" /> (which only sees lazy throws) cannot
    ///     catch them. Typed for the same reason: every other fault stays opaque.
    /// </summary>
    private static IAsyncEnumerable<ChatStreamEvent> RejectInvalidRequest(Func<IAsyncEnumerable<ChatStreamEvent>> start)
    {
        try
        {
            return start();
        }
        catch (NodeChatInvalidRequestException exception)
        {
            throw new HubException(exception.Message, exception);
        }
    }

    /// <summary>
    ///     Re-throws the stream services' TYPED caller-triggerable rejections as <see cref="HubException" />s.
    /// </summary>
    /// <remarks>
    ///     Matched by exception TYPE, never by a message string: widening it to every fault would forward internal
    ///     detail to the browser. The read-only and live-workflow rejections are PREFIXED with their
    ///     <see cref="NodeConflictProblemType" /> name — the discriminator the REST 409 carries as
    ///     <c>ConflictProblemDetails.conflictType</c> — via the enum, never a literal. The rejections are thrown LAZILY,
    ///     hence the manual enumeration. See docs/wiki/09-api-and-hubs.md ("Chat hub: attachment and refusals").
    /// </remarks>
    private static async IAsyncEnumerable<ChatStreamEvent> TranslateDomainRejections(IAsyncEnumerable<ChatStreamEvent> source,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        await using (enumerator)
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (NodeChatReadOnlyConversationException exception)
                {
                    throw new HubException($"{NodeConflictProblemType.ReadOnlyConversation}: {exception.Message}", exception);
                }
                catch (NodeChatWorkflowRunLiveException exception)
                {
                    throw new HubException($"{NodeConflictProblemType.GraphWorkflowRunLiveInConversation}: {exception.Message}", exception);
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
