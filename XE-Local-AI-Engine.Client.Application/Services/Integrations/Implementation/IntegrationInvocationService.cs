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

        // 1. The credential and the trigger, in that order. A key row that is gone or revoked answers the SAME generic
        //    401 the authentication handler writes; anything trigger-shaped — unknown, disabled, or outside this key's
        //    allowlist — answers one 404, because a distinct code for "exists but not yours" would confirm the name.
        var key = await _keyStore.GetByPrefixAsync(request.KeyPrefix, cancellationToken).ConfigureAwait(false);
        if (key is null || key.RevokedAtUtc is not null)
        {
            return Rejected(IntegrationAcceptOutcome.Unauthorized, "Invalid integration API key.");
        }

        var triggerName = IIntegrationTriggerService.NormalizeName(request.TriggerName);
        var trigger = await _triggers.GetByNameAsync(triggerName, cancellationToken).ConfigureAwait(false);

        // The allowlist is parsed and scanned BEFORE the combined decision, never short-circuited behind the trigger
        // lookup. A `trigger is null || !Allows(...)` reads identically and behaves identically, but it does strictly
        // less work for a name that does not exist than for one that exists and is not allowlisted — which is a timing
        // signal for trigger-name existence behind two byte-identical 404s.
        var allowed = Allows(key, trigger?.Id ?? Guid.Empty);
        if (trigger is null || !trigger.Enabled || !allowed)
        {
            return Rejected(IntegrationAcceptOutcome.TriggerNotFound, TriggerNotFoundMessage);
        }

        // 2. The session gate, and the per-session mutual exclusion around EVERYTHING that follows. A continuation
        //    holds its session's gate from resolution through the accept transaction's return and the seed write: the
        //    admission transaction bounds the node and the principal but counts nothing per session, so two accepts
        //    that both read "not busy" would both write a seed into the SAME conversation and the first execution
        //    would read the second caller's input as history.
        //
        //    A PerInvocation accept and a NEW caller-managed session name no session, so they take no gate — nothing
        //    else can name a session that does not exist yet.
        var caller = new IntegrationCallerIdentity(key.PrincipalId, request.KeyPrefix);
        var gateLease = request.SessionId is { } gatedSessionId
            ? await _sessionGate.EnterAsync(gatedSessionId, cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            // 3. Dedup, scoped to (principal, request id) — the pair the unique index covers. A FOREIGN request id is
            //    simply not found, so one integrator can never preclaim another's and force it a permanent 409.
            //
            //    It runs BEFORE session RESOLUTION and before the input checks — inside the per-session gate, which is
            //    still entered first and held through admission — and that order is the whole point of
            //    `requestId`: a retry happens exactly when the original 202 was lost, which is exactly when the
            //    original execution is still running on the session it named. Resolving the session first answered
            //    such a retry with SessionBusy 409, and a session closed since answered SessionClosed — in both cases
            //    hiding the execution id the caller was retrying to learn. Nothing here needs the session: the
            //    fingerprint covers the principal, the trigger name, the requested session id and the raw body.
            var fingerprint = IntegrationRequestFingerprint.Compute(key.PrincipalId, triggerName, request.SessionId, request.RawBody.Span);
            var duplicate = await ResolveDuplicateAsync(key.PrincipalId, request.RequestId, fingerprint, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                return duplicate;
            }

            var gate = await _sessions.ResolveForInvocationAsync(request.SessionId, trigger, caller, cancellationToken).ConfigureAwait(false);
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

            return await AdmitAsync(request, key.PrincipalId, trigger, gate.Existing, seed, fingerprint, cancellationToken).ConfigureAwait(false);
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
        // 5. Mint the ids and the buffer entry. For a NEW session the conversation id is minted HERE and recorded
        //    inside the admission transaction; step 7 creates the conversation at exactly that id, which is what makes
        //    an orphan conversation impossible rather than merely unlikely. A CONTINUATION mints neither: it joins the
        //    existing session and writes its seed into that session's existing conversation.
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
            var acceptedEvent = new IntegrationEventAppend(Guid.NewGuid(),
                executionId,
                accepted.Sequence,
                accepted.Type,
                DetailJson: null,
                accepted.OccurredAtUtc);

            // 6. One raw-connection transaction under BEGIN IMMEDIATE: the write lock is taken before the counts are
            //    read, which is what makes the advertised bound the enforced bound.
            // A null NewSession is what tells the store this is a continuation: it bumps the existing row's
            // ExecutionCount and LastActivityUtc inside the same commit, scoped to the caller's own Active session, and
            // throws IntegrationSessionUnavailableException if that scoped update matches nothing — the race-free
            // backstop behind the gate's own pre-checks.
            var command = new IntegrationAcceptCommand(existingSession is null
                    ? new IntegrationSessionCreate(sessionId, trigger.Id, conversationId, trigger.TargetAgentDefinitionId)
                    : null,
                executionId,
                trigger.Id,
                sessionId,
                principalId,
                request.RequestId,
                fingerprint,
                request.KeyPrefix,
                receivedAtUtc,
                acceptedEvent);

            bool committed;
            try
            {
                committed = await _executions.AcceptAsync(command, _options.MaxQueuedExecutions, _options.MaxQueuedExecutionsPerPrincipal, cancellationToken)
                                             .ConfigureAwait(false);
            }
            catch (IntegrationQueueFullException)
            {
                return Rejected(IntegrationAcceptOutcome.QueueFull, "The node is at its concurrent execution limit.");
            }
            catch (IntegrationSessionUnavailableException)
            {
                // The store's scoped session update matched no row: the session went missing, changed hands or closed
                // between the gate's pre-check and this transaction. The gate answers the precise 404/409 on every
                // path a caller can actually reach; this is the race-free backstop, and it answers the masked 404
                // rather than confirming which of the three it was.
                return Rejected(IntegrationAcceptOutcome.SessionNotFound, "No such session.");
            }
            catch (Exception exception) when (exception is DbUpdateException or SqliteException { SqliteErrorCode: SqliteConstraintErrorCode })
            {
                // A concurrent accept from the same principal won the (PrincipalId, RequestId) race. Re-read the
                // winner and answer it as a duplicate or a conflict rather than surfacing a 500.
                //
                // AcceptAsync is raw ADO under BEGIN IMMEDIATE with no SaveChanges anywhere, so the unique index
                // surfaces as SqliteException, NOT as the EF-only DbUpdateException. Both are caught: the EF type
                // stays for a future store that does go through a DbContext.
                var raced = await ResolveDuplicateAsync(principalId, request.RequestId, fingerprint, cancellationToken).ConfigureAwait(false);
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
            // CancellationToken.None on both: past the commit the work is no longer the caller's to cancel. A client
            // that disconnects here would otherwise leave an Accepted row that was never enqueued, and the admission
            // cap counts it against its principal until the next restart sweep.
            if (existingSession is null)
            {
                _ = await _persistence.CreateConversationAsync(new NodeChatCreateConversationRequest(trigger.DisplayName,
                                              UserId: null,
                                              receivedAtUtc,
                                              NodeChatOriginValues.Local,
                                              trigger.TargetAgentDefinitionId,
                                              NodeConversationKind.Integration,
                                              conversationId),
                                          CancellationToken.None)
                                      .ConfigureAwait(false);
            }

            // The seed message id IS the execution id, so a continuation can address the seed turn with no lookup and
            // no extra column. One execution owns exactly one seed, so the ids cannot collide.
            _ = await _persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversationId, executionId, seed, receivedAtUtc), CancellationToken.None)
                                  .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Runs FORWARD, never backward. The row is committed and Accepted; it is not compensated and not deleted.
            // Enqueue it anyway so the coordinator picks it up, finds no conversation, and terminalises it with a real
            // reason instead of leaving it to the next restart sweep.
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
                await TerminalizeQueueFullAsync(executionId, sessionId).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The caller is getting its 503 either way. Letting this out instead would turn a refused admission
                // into a 500 AND leave the row Accepted with no answer at all.
                _logger.LogError(exception, "Integration execution {ExecutionId} could not be terminalized after the queue refused it.", executionId);
            }

            return Rejected(IntegrationAcceptOutcome.QueueFull, "The node is at its concurrent execution limit.");
        }

        return new IntegrationAcceptResult(IntegrationAcceptOutcome.Accepted, executionId, sessionId, IntegrationExecutionStatus.Accepted, "Accepted.");
    }

    /// <summary>
    ///     The one place an accept terminalises its own row: the queue refused an admitted execution. Reserve the
    ///     sequence, commit the terminal status and its event in one transaction, then publish — and abandon the
    ///     reservation if that transaction did not happen, so no reader is left parked at a barrier that never
    ///     resolves.
    /// </summary>
    private async Task TerminalizeQueueFullAsync(Guid executionId, Guid sessionId)
    {
        var sequence = _buffer.Reserve(executionId);
        var endedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var resolved = false;
        // The same payload on the row and on the frame, exactly as the coordinator's terminal does it.
        var payload = IntegrationTerminalPayload.Failure(IntegrationFailureCategories.QueueFull, QueueFullSummary);

        try
        {
            var terminalized = await _executions.TryTerminalizeAsync(new IntegrationTerminalizeCommand(executionId,
                                                        ExpectedVersion: 0,
                                                        new HashSet<IntegrationExecutionStatus>
                                                        {
                                                            IntegrationExecutionStatus.Accepted
                                                        },
                                                        IntegrationExecutionStatus.Failed,
                                                        sequence,
                                                        IntegrationStreamEventTypes.ExecutionFailed,
                                                        endedAtUtc,
                                                        IntegrationFailureCategories.QueueFull,
                                                        QueueFullSummary,
                                                        payload.GetRawText()),
                                                    CancellationToken.None)
                                                .ConfigureAwait(false);

            if (terminalized)
            {
                _buffer.Publish(new IntegrationStreamEvent(IntegrationStreamEventTypes.ExecutionFailed,
                    sequence,
                    executionId,
                    sessionId,
                    endedAtUtc,
                    ContentType: null,
                    payload));
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
        var existing = await _executions.GetByRequestIdAsync(principalId, requestId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        return CryptographicOperations.FixedTimeEquals(existing.RequestFingerprint.Span, fingerprint)
            ? new IntegrationAcceptResult(IntegrationAcceptOutcome.Duplicate, existing.Id, existing.SessionId, existing.Status, "Duplicate request.")
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
        new(outcome, ExecutionId: null, SessionId: null, Status: null, message);
}
