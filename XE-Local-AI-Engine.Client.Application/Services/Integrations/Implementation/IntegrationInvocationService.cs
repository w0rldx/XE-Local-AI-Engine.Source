namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>Default <see cref="IIntegrationInvocationService" />. The step order is ruling R4-1's and the comments name why each step sits where it does.</summary>
internal sealed class IntegrationInvocationService : IIntegrationInvocationService
{
    private const string TriggerNotFoundMessage = "No such trigger.";

    /// <summary>One literal, because the row's column and the terminal frame's payload must say the same thing.</summary>
    private const string QueueFullSummary = "The execution queue refused the admitted request.";

    /// <summary>SQLite's <c>SQLITE_CONSTRAINT</c>. The unique index is the only constraint this path can violate.</summary>
    private const int SqliteConstraintErrorCode = 19;

    private readonly IIntegrationExecutionEventBuffer _buffer;
    private readonly IIntegrationExecutionStore _executions;
    private readonly IIntegrationApiKeyService _keys;
    private readonly IIntegrationApiKeyStore _keyStore;
    private readonly ILogger<IntegrationInvocationService> _logger;
    private readonly IntegrationOptions _options;
    private readonly INodeChatPersistenceService _persistence;
    private readonly Channel<Guid> _queue;
    private readonly IntegrationSessionGate _sessionGate;
    private readonly IntegrationSessionService _sessions;
    private readonly TimeProvider _timeProvider;
    private readonly IIntegrationTriggerStore _triggers;

    public IntegrationInvocationService(IIntegrationTriggerStore triggers,
        IIntegrationApiKeyStore keyStore,
        IIntegrationApiKeyService keys,
        IIntegrationExecutionStore executions,
        IIntegrationExecutionEventBuffer buffer,
        INodeChatPersistenceService persistence,
        IntegrationSessionService sessions,
        IntegrationSessionGate sessionGate,
        Channel<Guid> queue,
        IOptions<IntegrationOptions> options,
        TimeProvider timeProvider,
        ILogger<IntegrationInvocationService> logger)
    {
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionGate = sessionGate ?? throw new ArgumentNullException(nameof(sessionGate));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IntegrationAcceptResult> AcceptAsync(IntegrationAcceptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. The credential and the trigger, in that order. A key row that is gone or revoked answers the SAME generic 401 the authentication handler writes;
        //    anything trigger-shaped — unknown, disabled, or outside this key's allowlist — answers one 404: "exists but not yours" would confirm the name.
        var key = await _keyStore.GetByPrefixAsync(request.KeyPrefix, cancellationToken);
        if (key is null || key.RevokedAtUtc is not null)
        {
            return Rejected(IntegrationAcceptOutcome.Unauthorized, "Invalid integration API key.");
        }

        var triggerName = IIntegrationTriggerService.NormalizeName(request.TriggerName);
        var trigger = await _triggers.GetByNameAsync(triggerName, cancellationToken);

        // The allowlist is parsed and scanned BEFORE the combined decision, never short-circuited behind the trigger lookup: short-circuiting does strictly
        // less work for a name that does not exist, which is a timing signal for trigger-name existence behind two byte-identical 404s.
        var allowed = Allows(key, trigger?.Id ?? Guid.Empty);
        if (trigger is null || !trigger.Enabled || !allowed)
        {
            return Rejected(IntegrationAcceptOutcome.TriggerNotFound, TriggerNotFoundMessage);
        }

        // 2. The session gate, held from resolution through the accept transaction and the seed write; an accept naming no session takes none. Admission bounds
        //    the node and the principal but nothing per session, so two accepts reading "not busy" would seed one conversation and run on each other's input.
        var caller = new IntegrationCallerIdentity { PrincipalId = key.PrincipalId, KeyPrefix = request.KeyPrefix };
        var gateLease = request.SessionId is { } gatedSessionId
            ? await _sessionGate.EnterAsync(gatedSessionId, cancellationToken)
            : null;
        try
        {
            // 3. Dedup, scoped to (principal, request id) — the pair the unique index covers, so a FOREIGN request id is simply not found and no integrator
            //    can preclaim another's. It runs BEFORE session resolution and the input checks, inside the gate: ADR 0008 ("The accept path's ordering").
            var fingerprint = IntegrationRequestFingerprint.Compute(key.PrincipalId, triggerName, request.SessionId, request.RawBody.Span);
            var duplicate = await ResolveDuplicateAsync(key.PrincipalId, request.RequestId, fingerprint, cancellationToken);
            if (duplicate is not null)
            {
                return duplicate;
            }

            var gate = await _sessions.ResolveForInvocationAsync(request.SessionId, trigger, caller, cancellationToken);
            if (gate.Outcome != IntegrationAcceptOutcome.Accepted)
            {
                return Rejected(gate.Outcome, gate.Message);
            }

            // 4. Inputs, then the composed seed measured against its ceiling. The composer never truncates: silently
            //    trimming an external payload changes the meaning of the request without telling the caller.
            if (!AcceptsInputs(trigger, request.Inputs, out var inputsMessage))
            {
                return Rejected(IntegrationAcceptOutcome.InputsRejected, inputsMessage);
            }

            var seed = IntegrationSeedComposer.Compose(request.Inputs);
            if (IntegrationSeedComposer.Utf8ByteCount(seed) > _options.MaxSeedBytes)
            {
                return Rejected(IntegrationAcceptOutcome.InputsRejected, "The composed request is larger than this node accepts.");
            }

            return await AdmitAsync(request, key.PrincipalId, trigger, gate.Existing, seed, fingerprint, cancellationToken);
        }
        finally
        {
            gateLease?.Dispose();
        }
    }

    private async Task<IntegrationAcceptResult> AdmitAsync(IntegrationAcceptRequest request,
        Guid principalId,
        IntegrationTriggerSnapshot trigger,
        IntegrationSessionSnapshot? existingSession,
        string seed,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        // 5. Mint the ids and the buffer entry. A NEW session's conversation id is minted HERE and recorded inside the admission transaction, and step 7
        //    creates the conversation at exactly that id, which is what makes an orphan conversation impossible rather than merely unlikely.
        var executionId = Guid.NewGuid();
        var sessionId = existingSession?.Id ?? Guid.NewGuid();
        var conversationId = existingSession?.ConversationId ?? Guid.NewGuid();
        var receivedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (!_buffer.TryCreate(executionId))
        {
            return Rejected(IntegrationAcceptOutcome.QueueFull, "The node is at its concurrent execution limit.");
        }

        var admitted = false;
        try
        {
            // The buffer is the only minter of a sequence, so the accepted event is minted here and CARRIED into the
            // command — the number that reaches the row is provably the one the buffer returned.
            var accepted = _buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionAccepted, contentType: null, payload: null);
            var acceptedEvent = new IntegrationEventAppend
            {
                EventId = Guid.NewGuid(),
                ExecutionId = executionId,
                Sequence = accepted.Sequence,
                EventType = accepted.Type,
                DetailJson = null,
                OccurredAtUtc = accepted.OccurredAtUtc
            };

            // 6. One raw-connection transaction under BEGIN IMMEDIATE, so the write lock is taken before the counts are read and the advertised bound is the
            //    enforced one. A null NewSession tells the store this is a continuation: it bumps that row in the same commit, scoped to the caller's own Active session.
            var command = new IntegrationAcceptCommand
            {
                NewSession = existingSession is null
                    ? new IntegrationSessionCreate { SessionId = sessionId, TriggerId = trigger.Id, ConversationId = conversationId, AgentDefinitionId = trigger.TargetAgentDefinitionId }
                    : null,
                ExecutionId = executionId,
                TriggerId = trigger.Id,
                SessionId = sessionId,
                PrincipalId = principalId,
                RequestId = request.RequestId,
                RequestFingerprint = fingerprint,
                KeyPrefix = request.KeyPrefix,
                ReceivedAtUtc = receivedAtUtc,
                AcceptedEvent = acceptedEvent
            };

            bool committed;
            try
            {
                committed = await _executions.AcceptAsync(command, _options.MaxQueuedExecutions, _options.MaxQueuedExecutionsPerPrincipal, cancellationToken);
            }
            catch (IntegrationQueueFullException)
            {
                return Rejected(IntegrationAcceptOutcome.QueueFull, "The node is at its concurrent execution limit.");
            }
            catch (IntegrationSessionUnavailableException)
            {
                // The store's scoped session update matched no row: the session went missing, changed hands or closed between the gate's pre-check and this
                // transaction. The gate answers the precise 404/409 on every reachable path; this backstop answers the masked 404, never which of the three.
                return Rejected(IntegrationAcceptOutcome.SessionNotFound, "No such session.");
            }
            catch (Exception exception) when (exception is DbUpdateException or SqliteException { SqliteErrorCode: SqliteConstraintErrorCode })
            {
                // A concurrent accept from the same principal won the (PrincipalId, RequestId) race: re-read the winner and answer duplicate or conflict
                // rather than a 500. Raw ADO surfaces the unique index as SqliteException, not DbUpdateException; the EF type stays for a future DbContext store.
                var raced = await ResolveDuplicateAsync(principalId, request.RequestId, fingerprint, cancellationToken);
                return raced ?? Rejected(IntegrationAcceptOutcome.RequestConflict, "That request id was used with a different body.");
            }

            if (!committed)
            {
                // The credential was revoked between authentication and this transaction. Nothing was written, and the
                // caller gets the same generic 401 as any other invalid credential.
                return Rejected(IntegrationAcceptOutcome.Unauthorized, "Invalid integration API key.");
            }

            admitted = true;
        }
        finally
        {
            // Every rejection BEFORE the commit releases the tracked slot; a reservation left behind holds one that
            // only a terminal event could free, and this execution will never get one.
            if (!admitted)
            {
                _buffer.Remove(executionId);
            }
        }

        // 7. Only after the commit: the owned conversation, then the seed. Both go through a singleton persistence
        //    service that opens its own scope, so they cannot join the transaction above and must not try to.
        try
        {
            // CancellationToken.None on both: past the commit the work is no longer the caller's to cancel. A client that disconnects here would otherwise
            // leave an Accepted row that was never enqueued, counted against its principal's admission cap until the next restart sweep.
            if (existingSession is null)
            {
                _ = await _persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
                {
                    Title = trigger.DisplayName,
                    UserId = null,
                    CreatedAtUtc = receivedAtUtc,
                    Origin = NodeChatOriginValues.Local,
                    AgentDefinitionId = trigger.TargetAgentDefinitionId,
                    Kind = NodeConversationKind.Integration,
                    ConversationId = conversationId
                },
                                          CancellationToken.None);
            }

            // The seed message id IS the execution id, so a continuation can address the seed turn with no lookup and
            // no extra column. One execution owns exactly one seed, so the ids cannot collide.
            _ = await _persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest { ConversationId = conversationId, MessageId = executionId, Content = seed, CreatedAtUtc = receivedAtUtc }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Runs FORWARD, never backward: the row is committed and Accepted, and is neither compensated nor deleted. Enqueue it anyway so the coordinator
            // picks it up, finds no conversation and terminalises it with a real reason instead of leaving it to the next restart sweep.
            _logger.LogError(exception,
                "Integration execution {ExecutionId} was admitted but its owned conversation or seed could not be written; the coordinator will terminalize it.",
                executionId);
            _ = _queue.Writer.TryWrite(executionId);
            throw;
        }

        // 8. Enqueue. The channel is bounded with FullMode.Wait, so a full channel returns false here instead of
        //    accepting and silently discarding the id, which would strand an Accepted row nothing would ever drain.
        if (!_queue.Writer.TryWrite(executionId))
        {
            try
            {
                await TerminalizeQueueFullAsync(executionId, sessionId);
            }
            catch (Exception exception)
            {
                // The caller is getting its 503 either way. Letting this out instead would turn a refused admission
                // into a 500 AND leave the row Accepted with no answer at all.
                _logger.LogError(exception, "Integration execution {ExecutionId} could not be terminalized after the queue refused it.", executionId);
            }

            return Rejected(IntegrationAcceptOutcome.QueueFull, "The node is at its concurrent execution limit.");
        }

        return new IntegrationAcceptResult { Outcome = IntegrationAcceptOutcome.Accepted, ExecutionId = executionId, SessionId = sessionId, Status = IntegrationExecutionStatus.Accepted, Message = "Accepted." };
    }

    /// <summary>The one place an accept terminalises its own row: the queue refused an admitted execution.</summary>
    /// <remarks>
    ///     Reserve the sequence, commit the terminal status and its event in one transaction, then publish — and
    ///     abandon the reservation if that transaction did not happen, so no reader is left parked at a barrier that
    ///     never resolves.
    /// </remarks>
    private async Task TerminalizeQueueFullAsync(Guid executionId, Guid sessionId)
    {
        var sequence = _buffer.Reserve(executionId);
        var endedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var resolved = false;
        // The same payload on the row and on the frame, exactly as the coordinator's terminal does it.
        var payload = IntegrationTerminalPayload.Failure(IntegrationFailureCategories.QueueFull, QueueFullSummary);

        try
        {
            var terminalized = await _executions.TryTerminalizeAsync(new IntegrationTerminalizeCommand
            {
                ExecutionId = executionId,
                ExpectedVersion = 0,
                ExpectedStatuses = new HashSet<IntegrationExecutionStatus>
                                                        {
                                                            IntegrationExecutionStatus.Accepted
                                                        },
                NewStatus = IntegrationExecutionStatus.Failed,
                Sequence = sequence,
                EventType = IntegrationStreamEventTypes.ExecutionFailed,
                EndedAtUtc = endedAtUtc,
                FailureCategory = IntegrationFailureCategories.QueueFull,
                FailureSummary = QueueFullSummary,
                EventDetailJson = payload.GetRawText()
            },
                                                    CancellationToken.None);

            if (terminalized)
            {
                _buffer.Publish(new IntegrationStreamEvent
                {
                    Type = IntegrationStreamEventTypes.ExecutionFailed,
                    Sequence = sequence,
                    ExecutionId = executionId,
                    SessionId = sessionId,
                    OccurredAtUtc = endedAtUtc,
                    ContentType = null,
                    Payload = payload
                });
                resolved = true;
            }
        }
        finally
        {
            if (!resolved)
            {
                _buffer.Abandon(executionId, sequence);
            }

            // Nothing will read this execution: the caller got a 503 and never learned an id.
            _buffer.Remove(executionId);
        }
    }

    private async Task<IntegrationAcceptResult?> ResolveDuplicateAsync(Guid principalId,
        Guid requestId,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        var existing = await _executions.GetByRequestIdAsync(principalId, requestId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        return CryptographicOperations.FixedTimeEquals(existing.RequestFingerprint.Span, fingerprint)
            ? new IntegrationAcceptResult { Outcome = IntegrationAcceptOutcome.Duplicate, ExecutionId = existing.Id, SessionId = existing.SessionId, Status = existing.Status, Message = "Duplicate request." }
            : Rejected(IntegrationAcceptOutcome.RequestConflict, "That request id was used with a different body.");
    }

    /// <summary>The allowlist rule, stated once: a null column allows every trigger, and a list allows exactly what it names.</summary>
    private static bool Allows(IntegrationApiKeySnapshot key, Guid triggerId)
    {
        var allowed = IntegrationApiKeyService.DeserializeAllowList(key.AllowedTriggerIdsJson);
        return allowed is null || allowed.Contains(triggerId);
    }

    private static bool AcceptsInputs(IntegrationTriggerSnapshot trigger, IReadOnlyList<IntegrationInputDto> inputs, out string message)
    {
        if (inputs.Count == 0)
        {
            message = "Send at least one input.";
            return false;
        }

        foreach (var input in inputs)
        {
            // An input has to name exactly one KNOWN kind, and the trigger has to accept it. The "known" half is
            // what stops an unset or invented flag value passing the mask trivially.
            var known = input.Kind is IntegrationInputKinds.Text or IntegrationInputKinds.Json;
            if (!known || (trigger.AcceptedInputKinds & input.Kind) != input.Kind)
            {
                message = "This trigger does not accept one of the supplied input kinds.";
                return false;
            }

            var missingContent = input.Kind == IntegrationInputKinds.Json
                ? string.IsNullOrWhiteSpace(input.Json)
                : string.IsNullOrWhiteSpace(input.Text);
            if (missingContent)
            {
                message = "An input carried no content.";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static IntegrationAcceptResult Rejected(IntegrationAcceptOutcome outcome, string message) =>
        new() { Outcome = outcome, ExecutionId = null, SessionId = null, Status = null, Message = message };
}
