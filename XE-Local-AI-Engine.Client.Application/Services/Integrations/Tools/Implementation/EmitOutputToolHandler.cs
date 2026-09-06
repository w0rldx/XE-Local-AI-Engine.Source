namespace XE_Local_AI_Engine.Client.Services.Integrations.Tools.Implementation;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     The one built-in tool an integration execution is additionally offered: it hands a typed payload to the external
///     caller that started the run.
///     <para>
///         <b>Durable before visible.</b> The order is read the counter fresh, refuse over-cap, <c>Reserve</c> a
///         sequence, commit the row with it, and only then <c>Publish</c>. An <c>external.output</c> frame is an
///         instruction a robot may act on: published before its row commits it could name a result absent from durable
///         history, from the execution's counters, from audit inspection and from restart recovery — and terminalizing
///         the run afterwards does not un-actuate anything the caller already did.
///     </para>
///     <para>
///         <b>Exactly one of Publish or Abandon follows every successful Reserve.</b> A reservation holds every reader
///         of that execution at its sequence, so an unresolved one is not a hole readers tolerate — it is a stall for
///         the life of the entry. An ABANDONED hole is legal: <c>Last-Event-ID</c> is a watermark, not a dense index.
///     </para>
///     <para>
///         <b>Security posture.</b> The payload is opaque to this node: never parsed for meaning and never executed.
///         It is bounded per call and per execution, its media type is validated at the trust boundary because it is
///         echoed back in a header-shaped field, every delivered call increments the audited execution row's counters,
///         and the acknowledgement handed back to the model never echoes the payload. The one path that flows a payload
///         back to a model is the later-turn replay of a caller-managed session, and that goes inside an
///         untrusted-content fence.
///     </para>
/// </summary>
internal sealed partial class EmitOutputToolHandler : IClientLocalToolHandler
{
    private const string NotInIntegrationExecution = "This tool only works inside an integration execution.";

    private const string NoRunningExecution = "No integration execution is currently running for this session.";

    /// <summary>
    ///     The same options every other bounded local tool parses with: unmapped members are a REFUSAL, so a model that
    ///     invents a key gets a sentence naming the shape rather than having the extra silently dropped.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IIntegrationExecutionEventBuffer _buffer;
    private readonly ILogger<EmitOutputToolHandler> _logger;
    private readonly IntegrationOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public EmitOutputToolHandler(IServiceScopeFactory scopeFactory,
        IOptions<IntegrationOptions> options,
        IIntegrationExecutionEventBuffer buffer,
        TimeProvider timeProvider,
        ILogger<EmitOutputToolHandler> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ToolName => EmitOutputToolDefinition.ToolName;

    public string Description => EmitOutputToolDefinition.Description;

    public string ParameterSchema => EmitOutputToolDefinition.ParameterSchema;

    public bool RequiresApproval => false;

    /// <summary>
    ///     Every POLICY refusal returns a sentence the model can act on, because a throw inside the function-invocation
    ///     pipeline DESTROYS that sentence: MEAI's <c>FunctionInvokingChatClient</c> catches the exception and hands the
    ///     model its own fixed <c>Error: Function failed.</c> instead, with no detail at all under the pipeline's
    ///     <c>IncludeDetailedErrors=false</c> default. (Measured on MEAI 10.9.0: the throw does NOT end the turn — the
    ///     loop runs a further provider round — so the model would simply retry the same call, blind to why it failed.)
    ///     The ONE deliberate exception is a persistence FAILURE, which throws on purpose: that is what makes an
    ///     unbacked frame unrepeatable — the run terminalizes <c>Failed</c> / <c>internal-failure</c> and there is no
    ///     next call that could publish a second one.
    ///     <para>
    ///         Because a refusal is a normal RETURN, the pipeline reports the call as successful and
    ///         <c>IntegrationStreamEventMapper</c> would put <c>ok:true</c> on the caller's
    ///         <c>tool.completed</c> frame. That is why the acknowledgement below opens with
    ///         <see cref="EmitOutputToolDefinition.DeliveredPrefix" />: it is the only outcome signal that survives the
    ///         pipeline, and the mapper grades this one tool on it.
    ///     </para>
    /// </summary>
    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        if (jsonArguments.Length > EmitOutputToolDefinition.MaxJsonArgumentsLength)
        {
            return $"{ToolName} arguments exceeded the maximum length of {EmitOutputToolDefinition.MaxJsonArgumentsLength} characters.";
        }

        // The ambient conversation id the invocation runner seeds once per root tool loop — never an argument, which
        // would be model-forgeable. Every non-integration caller fails here or at the next step: the scheduler and the
        // benchmark executors pass a throwaway conversation id, and an MCP run seeds no ambient at all.
        if (AgentRunConversationContext.Current is not { } conversationId)
        {
            return NotInIntegrationExecution;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<IIntegrationSessionStore>()
                                 .FindByConversationAsync(conversationId, cancellationToken)
                                 .ConfigureAwait(false);
        if (session is null)
        {
            return NotInIntegrationExecution;
        }

        var executionStore = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
        var execution = await executionStore.FindActiveBySessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            return NoRunningExecution;
        }

        EmitOutputRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<EmitOutputRequest>(jsonArguments, SerializerOptions);
        }
        catch (JsonException)
        {
            // The exception is deliberately NOT attached. Under UnmappedMemberHandling.Disallow the parser's message
            // quotes the unexpected PROPERTY NAME, which the model produced and which can therefore carry response
            // content — logging it would break the no prompt/request/response content rule. A fixed sentence plus the
            // ids is everything an operator can act on anyway; the model gets a shape it can copy.
            _logger.LogDebug("{ToolName} could not read its arguments for integration execution {ExecutionId}.", ToolName, execution.Id);
            return $"{ToolName} arguments were not valid JSON for this tool. Send exactly this shape and no other keys: "
                   + """{"contentType": "application/json", "payload": {"ok": true}}""";
        }

        if (request is null || request.Payload is not { } payload)
        {
            return $"{ToolName} needs a payload.";
        }

        var contentType = string.IsNullOrWhiteSpace(request.ContentType)
            ? EmitOutputToolDefinition.DefaultContentType
            : request.ContentType.Trim();

        // Validated at the trust boundary because it is echoed back to the caller in a header-shaped field, not because
        // this node interprets it.
        if (!MediaType().IsMatch(contentType))
        {
            return $"'{contentType}' is not a media type. Send something like 'application/json' or omit contentType.";
        }

        // Compose the DURABLE payload first, then measure IT — not the raw payload. The event's column is capped and
        // encrypted, so a payload just under the limit plus its wrapper plus a nonce and auth tag would overrun a bound
        // this handler claims to respect.
        var detailJson = JsonSerializer.Serialize(new EmitOutputEnvelope(contentType, payload), SerializerOptions);
        var plaintextBytes = (long)Encoding.UTF8.GetByteCount(detailJson);
        if (plaintextBytes > _options.MaxOutputBytes)
        {
            return $"That payload is {Encoding.UTF8.GetByteCount(payload.GetRawText())} bytes and its envelope is {plaintextBytes}; "
                   + $"the limit is {_options.MaxOutputBytes}. Nothing was delivered — send a smaller payload.";
        }

        // The aggregate pre-check, read FRESH from the execution row on every call. That column is the only authority:
        // a call commits before it publishes, so by the time a second call can begin the first call's bytes are already
        // in it, and an in-memory tally on top would double-count every committed call. Race-free per execution because
        // tool calls within one invocation are sequential (concurrent invocation is never enabled), and the store's own
        // in-transaction reserve is the backstop if that ever changes.
        var delivered = execution.OutputBytes;
        if (delivered + plaintextBytes > _options.MaxOutputBytesPerExecution)
        {
            return AggregateCapRefusal(delivered);
        }

        return await DeliverAsync(executionStore, execution, session.Id, contentType, payload, detailJson, plaintextBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DeliverAsync(IIntegrationExecutionStore executionStore,
        IntegrationExecutionSnapshot execution,
        Guid sessionId,
        string contentType,
        JsonElement payload,
        string detailJson,
        long plaintextBytes,
        CancellationToken cancellationToken)
    {
        // Reserve mints a sequence and publishes NOTHING. A throw here took no reservation, so there is nothing to
        // abandon — calling Abandon with a sequence Reserve never returned would be a defect, not defensive coding.
        //
        // It throws for an UNTRACKED id, which a post-terminal removal race can produce between the Running read above
        // and this line. The run is ending either way, so it answers with the same sentence every other refusal on
        // this path uses rather than escaping onto the runner thread. The persistence failure below still throws on
        // purpose — that is what makes an unbacked frame unrepeatable — and this case wrote nothing to be unbacked.
        long sequence;
        try
        {
            sequence = _buffer.Reserve(execution.Id);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogDebug(exception, "{ToolName} found no event buffer entry for execution {ExecutionId}.", ToolName, execution.Id);
            return NoRunningExecution;
        }

        var occurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var emitted = new IntegrationStreamEvent(IntegrationStreamEventTypes.ExternalOutput,
            sequence,
            execution.Id,
            sessionId,
            occurredAtUtc,
            contentType,
            payload);

        bool recorded;
        try
        {
            recorded = await executionStore.AppendOutputEventAsync(new IntegrationEventAppend(Guid.NewGuid(),
                                                   execution.Id,
                                                   sequence,
                                                   IntegrationStreamEventTypes.ExternalOutput,
                                                   detailJson,
                                                   occurredAtUtc),
                                               _options.MaxOutputBytesPerExecution,
                                               cancellationToken)
                                           .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Abandon BEFORE the rethrow, never after: the reservation holds every reader of this execution at this
            // sequence, so cleaning up later in the coordinator's terminalization would block the caller's stream for
            // as long as it stands.
            _buffer.Abandon(execution.Id, sequence);
            _logger.LogError(exception, "The external output of integration execution {ExecutionId} could not be persisted.", execution.Id);
            throw;
        }

        if (!recorded)
        {
            // The store's in-transaction reserve refused. Unreachable through the pre-check on a healthy node; this is
            // the defence-in-depth half, and it publishes nothing.
            _buffer.Abandon(execution.Id, sequence);
            return AggregateCapRefusal(execution.OutputBytes);
        }

        _buffer.Publish(emitted);
        return $"{EmitOutputToolDefinition.DeliveredPrefix} ({plaintextBytes} bytes, {contentType}). Do not repeat it in your reply.";
    }

    private string AggregateCapRefusal(long delivered) =>
        $"This execution has already delivered {delivered} of its {_options.MaxOutputBytesPerExecution} output bytes; nothing further was delivered.";

    /// <summary>A plausible media type, ordinal and lowercase. Not a full RFC grammar — a bound on what is echoed back.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9!#$&^_.+-]{0,126}/[a-z0-9][a-z0-9!#$&^_.+-]{0,126}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MediaType();

    private sealed record EmitOutputRequest(string? ContentType, JsonElement? Payload);

    /// <summary>
    ///     What is persisted and what a later turn replays: the declared media type beside the payload verbatim. It is
    ///     composed ONCE and both the cap check and the row use the same string, so the number checked and the number
    ///     stored cannot disagree.
    /// </summary>
    private sealed record EmitOutputEnvelope(
        [property: JsonPropertyName("contentType")]
        string ContentType,
        [property: JsonPropertyName("payload")]
        JsonElement Payload);
}
