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
/// </summary>
/// <remarks>
///     DURABLE BEFORE VISIBLE: read the counter fresh, refuse over-cap, <c>Reserve</c> a sequence, commit the row with it,
///     and only then <c>Publish</c> — an <c>external.output</c> frame is an instruction a robot may act on, and
///     terminalizing the run afterwards un-actuates nothing. Exactly one <c>Publish</c> or <c>Abandon</c> follows every
///     successful <c>Reserve</c>. The payload is opaque to this node: never parsed for meaning, never executed, bounded per
///     call and per execution. The rest of the posture: ADR 0008 ("emit_output is durable before visible").
/// </remarks>
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
    ///     Every POLICY refusal RETURNS a sentence the model can act on; only a persistence failure throws.
    /// </summary>
    /// <remarks>
    ///     A throw would destroy that sentence: MEAI 10.9.0's <c>FunctionInvokingChatClient</c> catches it and hands the
    ///     model its own fixed <c>Error: Function failed.</c> under the <c>IncludeDetailedErrors=false</c> default, and it
    ///     does NOT end the turn (measured), so the model retries the same call blind. A persistence failure throws on
    ///     purpose: that is what makes an unbacked frame unrepeatable. Because a refusal is a normal return the pipeline
    ///     reports success, so the acknowledgement opens with <see cref="EmitOutputToolDefinition.DeliveredPrefix" />.
    /// </remarks>
    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        if (jsonArguments.Length > EmitOutputToolDefinition.MaxJsonArgumentsLength)
        {
            return $"{ToolName} arguments exceeded the maximum length of {EmitOutputToolDefinition.MaxJsonArgumentsLength} characters.";
        }

        // The ambient conversation id the invocation runner seeds once per root tool loop — never an argument, which would be model-forgeable. Every
        // non-integration caller fails here or at the next step: the scheduler and benchmark executors pass a throwaway id, and an MCP run seeds no ambient.
        if (AgentRunConversationContext.Current is not { } conversationId)
        {
            return NotInIntegrationExecution;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<IIntegrationSessionStore>()
                                 .FindByConversationAsync(conversationId, cancellationToken);
        if (session is null)
        {
            return NotInIntegrationExecution;
        }

        var executionStore = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
        var execution = await executionStore.FindActiveBySessionAsync(session.Id, cancellationToken);
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
            // The exception is deliberately NOT attached: under UnmappedMemberHandling.Disallow the parser's message quotes the unexpected PROPERTY NAME, which
            // the model produced and which can carry response content, so logging it would break the no prompt/request/response content rule.
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

        // Compose the DURABLE payload first, then measure IT, never the raw payload: the event's column is capped and encrypted, so a payload just under the
        // limit plus its wrapper plus a nonce and auth tag would overrun a bound this handler claims to respect.
        var detailJson = JsonSerializer.Serialize(new EmitOutputEnvelope
        {
            ContentType = contentType,
            Payload = payload
        }, SerializerOptions);
        var plaintextBytes = (long)Encoding.UTF8.GetByteCount(detailJson);
        if (plaintextBytes > _options.MaxOutputBytes)
        {
            return $"That payload is {Encoding.UTF8.GetByteCount(payload.GetRawText())} bytes and its envelope is {plaintextBytes}; "
                   + $"the limit is {_options.MaxOutputBytes}. Nothing was delivered — send a smaller payload.";
        }

        // The aggregate pre-check, read FRESH from the execution row on every call: that column is the only authority, because a call commits before it
        // publishes and an in-memory tally would double-count. Tool calls within one invocation are sequential, and the store's in-transaction reserve backs it.
        var delivered = execution.OutputBytes;
        if (delivered + plaintextBytes > _options.MaxOutputBytesPerExecution)
        {
            return AggregateCapRefusal(delivered);
        }

        return await DeliverAsync(executionStore, execution, session.Id, contentType, payload, detailJson, plaintextBytes, cancellationToken);
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
        // Reserve mints a sequence and publishes NOTHING, so a throw here took no reservation and calling Abandon with a sequence it never returned would be a
        // defect. It throws for an UNTRACKED id — a post-terminal removal race — which refuses in the usual sentence: the run is ending and nothing was written.
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
        var emitted = new IntegrationStreamEvent
        {
            Type = IntegrationStreamEventTypes.ExternalOutput,
            Sequence = sequence,
            ExecutionId = execution.Id,
            SessionId = sessionId,
            OccurredAtUtc = occurredAtUtc,
            ContentType = contentType,
            Payload = payload
        };

        bool recorded;
        try
        {
            recorded = await executionStore.AppendOutputEventAsync(new IntegrationEventAppend
                {
                    EventId = Guid.NewGuid(),
                    ExecutionId = execution.Id,
                    Sequence = sequence,
                    EventType = IntegrationStreamEventTypes.ExternalOutput,
                    DetailJson = detailJson,
                    OccurredAtUtc = occurredAtUtc
                },
                _options.MaxOutputBytesPerExecution,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Abandon BEFORE the rethrow, never after: the reservation holds every reader of this execution at this sequence, so cleaning up later in the
            // coordinator's terminalization would block the caller's stream for as long as it stands.
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
    private sealed record EmitOutputEnvelope
    {
        [JsonPropertyName("contentType")]
        public required string ContentType { get; init; }

        [JsonPropertyName("payload")]
        public required JsonElement Payload { get; init; }
    }
}
