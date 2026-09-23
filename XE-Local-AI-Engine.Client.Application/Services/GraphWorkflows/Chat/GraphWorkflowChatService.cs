namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     A chat send in workflow mode, in a fixed order: validate, replay by request id, then dispatch on the conversation's
///     bound run. The two writes (the user message, then the start or answer) are separate commits.
/// </summary>
/// <remarks>
///     What makes a half-done send safe is the replay lookup plus the deterministic user-message id, not a lock: a retry
///     re-inserts nothing and completes whichever half is missing. One live run per conversation is the database's rule.
/// </remarks>
internal sealed class GraphWorkflowChatService : IGraphWorkflowChatService
{
    private const int MaxCancelAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConversationUploadedFileStore _files;
    private readonly INodeChatMutationGuard _guard;
    private readonly GraphWorkflowOptions _options;
    private readonly INodeChatPersistenceService _persistence;
    private readonly IInvocationResumeRegistry _resumeRegistry;
    private readonly IGraphWorkflowRunService _runs;
    private readonly SecurityOptions _security;
    private readonly IGraphWorkflowStore _store;
    private readonly TimeProvider _timeProvider;

    public GraphWorkflowChatService(IGraphWorkflowStore store,
        IGraphWorkflowRunService runs,
        INodeChatPersistenceService persistence,
        INodeChatMutationGuard guard,
        IInvocationResumeRegistry resumeRegistry,
        IConversationUploadedFileStore files,
        IOptions<GraphWorkflowOptions> options,
        IOptions<SecurityOptions> security,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _resumeRegistry = resumeRegistry ?? throw new ArgumentNullException(nameof(resumeRegistry));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _security = (security ?? throw new ArgumentNullException(nameof(security))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GraphWorkflowChatSendResult> SendAsync(Guid conversationId,
        GraphWorkflowChatSendRequest request,
        string? decidedBySubject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Validate. Nothing below this block writes until every refusal that can be known up front has had its chance.
        var content = request.Content?.Trim() ?? string.Empty;
        ValidateContent(request.RequestId, content);
        var definition = await _store.GetDefinitionAsync(request.DefinitionId, cancellationToken);
        if (definition.Kind != GraphWorkflowDefinitionKind.Chat)
        {
            throw new GraphWorkflowValidationException($"Graph workflow '{definition.Name}' is a {definition.Kind} workflow; only a Chat workflow runs from a conversation.");
        }

        await EnsureChatConversationAsync(conversationId, cancellationToken);
        await _guard.EnsureMutableAsync(conversationId, cancellationToken);
        var attachments = await ResolveAttachmentsAsync(conversationId, GraphWorkflowGraph.Parse(definition.GraphJson), request.AttachmentFileIds, cancellationToken);
        if (_resumeRegistry.TryGetLiveInvocationIdForConversation(conversationId) is not null)
        {
            throw new GraphWorkflowRunBusyException("A chat reply is still streaming in this conversation; wait for it or stop it first.");
        }

        var messageId = GraphWorkflowChatIds.UserMessage(request.RequestId);
        var input = JsonSerializer.Serialize(new RunInput { Message = content, Attachments = attachments, ConversationId = conversationId, MessageId = messageId }, JsonOptions);
        if (Encoding.UTF8.GetByteCount(input) > _options.MaxRunInputBytes)
        {
            throw new GraphWorkflowValidationException($"The message and its attachments are larger than the {_options.MaxRunInputBytes} bytes one run input may carry.");
        }

        // A request id is one send into one conversation: its deterministic user message living elsewhere is a caller bug, refused
        // before anything is written rather than failing the insert below as an unmapped error.
        if (await _persistence.GetMessageConversationIdAsync(messageId, cancellationToken) is { } owner && owner != conversationId)
        {
            throw new GraphWorkflowRunConflictException($"Request '{request.RequestId}' was already sent to another conversation.");
        }

        // 2. Replay: this request id already started a run, or already answered one. The same 202 again, with the missing half completed.
        if (await ReplayAsync(conversationId, request.RequestId, messageId, content, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        // 3. Dispatch on the conversation's newest bound run: the only one that can be live, since the index allows one.
        var newest = await NewestBoundRunAsync(conversationId, cancellationToken);
        if (newest is null || GraphWorkflowStateMachine.IsTerminal(newest.Status))
        {
            if (newest is not null && !request.ConfirmRerun && PinnedChatSettings(newest).RequireRerunConfirmation)
            {
                throw new GraphWorkflowRerunConfirmationRequiredException("This conversation already ran a workflow. Confirm to start another run.");
            }

            var started = await InsertThenAsync(conversationId,
                messageId,
                content,
                () => _runs.StartAsync(request.DefinitionId,
                    request.RequestId,
                    input,
                    definitionVersion: null,
                    new GraphWorkflowRunBinding { ConversationId = conversationId, TriggerMessageId = messageId },
                    cancellationToken),
                cancellationToken);
            return new GraphWorkflowChatSendResult { RunId = started.Run.Id, MessageId = messageId, Action = GraphWorkflowChatSendAction.Started };
        }

        if (await ParkedInputAsync(newest, cancellationToken) is not { } parked)
        {
            throw new GraphWorkflowRunBusyException($"The workflow in this conversation is {newest.Status}; stop it before sending another message.");
        }

        if (newest.DefinitionId != request.DefinitionId)
        {
            throw new GraphWorkflowRunConflictException("The workflow waiting for input in this conversation is not the one this message names.");
        }

        if (attachments.Count > 0)
        {
            throw new GraphWorkflowAttachmentsNotAcceptedException("A workflow's input request takes text only; send the attachments with a new run.");
        }

        var maxAnswerBytes = Math.Min(_security.MaxMessageSizeKb * 1024, _options.MaxOutputJsonBytes / 2);
        if (Encoding.UTF8.GetByteCount(content) > maxAnswerBytes)
        {
            throw new GraphWorkflowValidationException($"The answer is larger than the {maxAnswerBytes} bytes a workflow input request may carry.");
        }

        _ = await InsertThenAsync(conversationId,
            messageId,
            content,
            () => _runs.DecideAsync(newest.Id,
                parked,
                request.RequestId,
                GraphWorkflowDecisionKind.Answer,
                comment: null,
                JsonSerializer.Serialize(new AnswerPayload { Text = content }, JsonOptions),
                decidedBySubject,
                cancellationToken),
            cancellationToken);
        return new GraphWorkflowChatSendResult { RunId = newest.Id, MessageId = messageId, Action = GraphWorkflowChatSendAction.Answered };
    }

    public async Task<IReadOnlyList<GraphWorkflowChatBoundRun>> ListBoundRunsAsync(Guid conversationId, int limit, CancellationToken cancellationToken = default)
    {
        await EnsureChatConversationAsync(conversationId, cancellationToken);
        var runs = await _store.ListRunsByConversationAsync(conversationId, limit, cancellationToken);
        if (runs.Count == 0)
        {
            return [];
        }

        // Summaries, never the blobs: a name per definition without decrypting one.
        var names = (await _store.ListDefinitionsAsync(cancellationToken)).ToDictionary(static definition => definition.Id, static definition => definition.Name);
        var bound = new List<GraphWorkflowChatBoundRun>(runs.Count);
        foreach (var run in runs)
        {
            string? pendingKey = null;
            string? prompt = null;
            string? steerable = null;
            if (!GraphWorkflowStateMachine.IsTerminal(run.Status))
            {
                var nodeRuns = await _store.ListNodeRunsAsync(run.Id, cancellationToken);
                pendingKey = ParkedInputKey(run, nodeRuns);
                prompt = pendingKey is null ? null : PromptOf(run, pendingKey);
                steerable = nodeRuns.FirstOrDefault(static nodeRun => nodeRun.Kind is GraphWorkflowNodeKind.Agent or GraphWorkflowNodeKind.LlmCall
                                                                      && nodeRun.Status is GraphWorkflowNodeRunStatus.Queued or GraphWorkflowNodeRunStatus.Running)
                                   ?.NodeKey;
            }

            bound.Add(new GraphWorkflowChatBoundRun
            {
                Run = run,
                DefinitionName = names.GetValueOrDefault(run.DefinitionId),
                PendingInput = pendingKey is null ? null : new GraphWorkflowChatPendingInput { NodeKey = pendingKey, Prompt = prompt ?? string.Empty },
                SteerableNodeKey = steerable
            });
        }

        return bound;
    }

    public async Task CancelBoundRunAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        // Bounded: a version bump between the run service's read and write (a tick moving the run) loses the cancel, so the run
        // is re-read and asked again while it is still live and not already cancelling.
        for (var attempt = 0; attempt < MaxCancelAttempts; attempt++)
        {
            var newest = await NewestBoundRunAsync(conversationId, cancellationToken);
            if (newest is null || newest.Status == GraphWorkflowRunStatus.Cancelling || GraphWorkflowStateMachine.IsTerminal(newest.Status))
            {
                return;
            }

            try
            {
                _ = await _runs.CancelAsync(newest.Id, cancellationToken);
                return;
            }
            catch (GraphWorkflowRunConflictException)
            {
                // The run ended between the read and the cancel: nothing left to stop.
                return;
            }
            catch (GraphWorkflowInvalidTransitionException)
            {
                // A concurrent writer moved the run; re-read and ask again.
            }
        }
    }

    private async Task<GraphWorkflowRunSnapshot?> NewestBoundRunAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var runs = await _store.ListRunsByConversationAsync(conversationId, limit: 1, cancellationToken);
        return runs.Count == 0 ? null : runs[0];
    }

    private void ValidateContent(Guid requestId, string content)
    {
        if (requestId == Guid.Empty)
        {
            throw new GraphWorkflowValidationException("A workflow chat message needs a caller-minted request id.");
        }

        if (content.Length == 0)
        {
            throw new GraphWorkflowValidationException("A workflow chat message needs content.");
        }

        // The chat hub is the only other place this cap is enforced, and this endpoint does not pass through it.
        var maxBytes = _security.MaxMessageSizeKb * 1024;
        if (Encoding.UTF8.GetByteCount(content) > maxBytes)
        {
            throw new GraphWorkflowValidationException($"The message is larger than the {maxBytes} bytes a chat message may carry.");
        }
    }

    /// <summary>Exists and is an ordinary chat. The mutation guard a send adds reads only the origin, so these two are checked here.</summary>
    private async Task EnsureChatConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var kind = await _persistence.GetConversationKindAsync(conversationId, cancellationToken)
                   ?? throw new GraphWorkflowNotFoundException($"Conversation '{conversationId}' was not found.");
        if (!string.Equals(kind, NodeConversationKind.Chat, StringComparison.Ordinal))
        {
            throw new GraphWorkflowValidationException($"Conversation '{conversationId}' is a {kind} conversation; workflows run in ordinary chats only.");
        }
    }

    /// <summary>References only — id, name and kind; what a node does with them is S4's consumption.</summary>
    private async Task<IReadOnlyList<AttachmentReference>> ResolveAttachmentsAsync(Guid conversationId,
        GraphWorkflowGraph graph,
        IReadOnlyList<Guid> fileIds,
        CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return [];
        }

        if (graph.Chat is not { AcceptsAttachments: true })
        {
            throw new GraphWorkflowAttachmentsNotAcceptedException("This workflow does not accept attachments.");
        }

        var files = (await _files.ListAsync(conversationId, cancellationToken)).ToDictionary(static file => file.FileId);
        return [.. fileIds.Distinct().Select(fileId => files.TryGetValue(fileId, out var file)
            ? Reference(file)
            : throw new GraphWorkflowValidationException($"Attachment '{fileId}' is not a file of this conversation."))];
    }

    private static AttachmentReference Reference(ConversationUploadedFileInfo file) =>
        new()
        {
            FileId = file.FileId,
            Name = file.OriginalFileName,
            Kind = file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image" : "text"
        };

    private async Task<GraphWorkflowChatSendResult?> ReplayAsync(Guid conversationId,
        Guid requestId,
        Guid messageId,
        string content,
        CancellationToken cancellationToken)
    {
        if (await _store.FindRunByRequestAsync(requestId, cancellationToken) is { } started)
        {
            if (started.ConversationId != conversationId)
            {
                throw new GraphWorkflowRunConflictException($"Request '{requestId}' already started a run that is not bound to this conversation.");
            }

            _ = await InsertUserMessageAsync(conversationId, messageId, content, cancellationToken);
            return new GraphWorkflowChatSendResult { RunId = started.Id, MessageId = messageId, Action = GraphWorkflowChatSendAction.Started };
        }

        if (await _store.FindConversationDecisionAsync(conversationId, requestId, cancellationToken) is { } answered)
        {
            _ = await InsertUserMessageAsync(conversationId, messageId, content, cancellationToken);
            return new GraphWorkflowChatSendResult { RunId = answered.RunId, MessageId = messageId, Action = GraphWorkflowChatSendAction.Answered };
        }

        return null;
    }

    /// <summary>
    ///     Message first, then the run write. When the write loses a race or refuses the request (every validation throw is
    ///     pre-commit) and THIS call wrote the message, the message is removed again: a refused send leaves no orphan turn.
    /// </summary>
    private async Task<T> InsertThenAsync<T>(Guid conversationId, Guid messageId, string content, Func<Task<T>> write, CancellationToken cancellationToken)
    {
        var inserted = (await InsertUserMessageAsync(conversationId, messageId, content, cancellationToken)).Inserted;
        try
        {
            return await write();
        }
        catch (Exception exception) when (inserted && exception is GraphWorkflowRunBusyException
                                                                 or GraphWorkflowGateAlreadyDecidedException
                                                                 or GraphWorkflowRunConflictException
                                                                 or GraphWorkflowInvalidTransitionException
                                                                 or GraphWorkflowValidationException)
        {
            await _persistence.DeleteMessageAsync(conversationId, messageId, CancellationToken.None);
            throw;
        }
    }

    private Task<NodeChatInsertMessageIfAbsentResult> InsertUserMessageAsync(Guid conversationId, Guid messageId, string content, CancellationToken cancellationToken) =>
        _persistence.InsertMessageIfAbsentAsync(new NodeChatInsertMessageIfAbsentRequest
            {
                ConversationId = conversationId,
                MessageId = messageId,
                Role = "user",
                Content = content,
                CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            },
            cancellationToken);

    /// <summary>The node key of the <c>ChatInput</c> a waiting run is parked on, or null when it is busy or waits on a <c>Pause</c>.</summary>
    private async Task<string?> ParkedInputAsync(GraphWorkflowRunSnapshot run, CancellationToken cancellationToken) =>
        run.Status == GraphWorkflowRunStatus.WaitingForApproval ? ParkedInputKey(run, await _store.ListNodeRunsAsync(run.Id, cancellationToken)) : null;

    private static string? ParkedInputKey(GraphWorkflowRunSnapshot run, IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns) =>
        run.Status != GraphWorkflowRunStatus.WaitingForApproval
        || nodeRuns.Any(static nodeRun => nodeRun.Status is GraphWorkflowNodeRunStatus.Queued or GraphWorkflowNodeRunStatus.Running)
            ? null
            : nodeRuns.Where(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.WaitingForApproval && nodeRun.PendingDecisionKind == GraphWorkflowDecisionKind.Answer)
                      .Select(static nodeRun => nodeRun.NodeKey)
                      .Order(StringComparer.Ordinal)
                      .FirstOrDefault();

    private static string? PromptOf(GraphWorkflowRunSnapshot run, string nodeKey) =>
        GraphWorkflowGraph.Parse(run.GraphJson).Nodes.TryGetValue(nodeKey, out var node) && node.Config is GraphWorkflowChatInputConfig input ? input.Prompt : null;

    /// <summary>The chat settings the run PINNED, so a definition edited since cannot change what its last run asked for.</summary>
    private static GraphWorkflowChatSettings PinnedChatSettings(GraphWorkflowRunSnapshot run)
    {
        try
        {
            return GraphWorkflowGraph.Parse(run.GraphJson).Chat ?? GraphWorkflowChatSettings.Default;
        }
        catch (GraphWorkflowValidationException)
        {
            // A pinned graph this build no longer parses asks the safe question.
            return GraphWorkflowChatSettings.Default;
        }
    }

    /// <summary>The run input a chat send starts with: the message, attachment references, and where it came from.</summary>
    private sealed class RunInput
    {
        public required string Message { get; init; }

        public required IReadOnlyList<AttachmentReference> Attachments { get; init; }

        public required Guid ConversationId { get; init; }

        public required Guid MessageId { get; init; }
    }

    private sealed class AttachmentReference
    {
        public required Guid FileId { get; init; }

        public required string Name { get; init; }

        public required string Kind { get; init; }
    }

    private sealed class AnswerPayload
    {
        public required string Text { get; init; }
    }
}
