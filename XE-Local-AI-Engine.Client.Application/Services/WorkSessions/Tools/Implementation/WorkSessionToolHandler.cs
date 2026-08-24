namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     What one state-tool call committed: the sentence handed back to the model, and — when it wrote a row — the
///     watermark that write allocated plus what changed, so the base can announce it after the commit.
/// </summary>
internal sealed record WorkSessionToolOutcome(string Message, long? Sequence = null, WorkSessionChangeKind Kind = WorkSessionChangeKind.Status);

internal static class WorkSessionToolSerialization
{
    /// <summary>
    ///     Shared by every work-session handler, and non-generic on purpose: one copy of the options rather than one per
    ///     closed generic type.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

/// <summary>
///     The shared shape of the four work-session state tools: bounded JSON in, one sentence out, never a throw.
///     <para>
///         The session is resolved from the ambient conversation id the invocation runner seeds for the whole tool loop
///         (<see cref="AgentRunConversationContext" />) and a conversation-to-session lookup — never from the arguments,
///         which are model-authored. That is what makes the profile-opt-in offer safe: a work-session agent bound to an
///         ordinary chat resolves no session and gets four inert tools.
///     </para>
///     <para>
///         Every guard fails CLOSED to a sentence rather than an exception, because a throw inside the
///         function-invocation pipeline ends the turn where an actionable sentence lets the model recover.
///     </para>
/// </summary>
internal abstract class WorkSessionToolHandler<TRequest> : IClientLocalToolHandler
    where TRequest : class
{
    protected const string NotInWorkSession = "This tool only works inside a work session.";
    protected const string SessionClosed = "This work session is already closed, so nothing further can be recorded on it.";
    protected const string Disabled = "Work sessions are disabled on this node.";

    private const string ConcurrencyRefusalSuffix = " could not be recorded because the work session changed underneath it. Try the same call once more.";

    protected ILogger Logger { get; }
    private readonly IWorkSessionEventPublisher _publisher;
    private readonly IServiceScopeFactory _scopeFactory;

    protected WorkSessionToolHandler(IServiceScopeFactory scopeFactory,
        IOptions<WorkSessionOptions> options,
        IWorkSessionEventPublisher publisher,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Options = options.Value;
    }

    protected WorkSessionOptions Options { get; }

    public abstract string ToolName { get; }

    public abstract string Description { get; }

    public abstract string ParameterSchema { get; }

    /// <summary>
    ///     One concrete, valid argument payload for this tool, handed back verbatim whenever the arguments could not be
    ///     read. A small model that got the shape wrong recovers from an example far more reliably than from a
    ///     description of the shape — and the alternative, echoing the parser's own message, spends the step's whole
    ///     call budget telling it the name of a CLR type.
    /// </summary>
    protected abstract string ExampleArguments { get; }

    /// <summary>
    ///     Auto-execute. These tools write only into the session's own rows, and an approval prompt per finding would
    ///     make an unattended session unusable. The node-level policy can still tighten the whole
    ///     <see cref="ToolCategory.WriteExecute" /> category, which is a deliberate consequence of labelling them
    ///     honestly rather than hiding the write behind <see cref="ToolCategory.ReadLocal" />.
    /// </summary>
    public bool RequiresApproval => false;

    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Options.Enabled)
        {
            return Disabled;
        }

        if (jsonArguments.Length > WorkSessionToolDefinitions.MaxJsonArgumentsLength)
        {
            return $"{ToolName} argument payload exceeded the maximum length of {WorkSessionToolDefinitions.MaxJsonArgumentsLength} characters.";
        }

        if (AgentRunConversationContext.Current is not { } conversationId)
        {
            return NotInWorkSession;
        }

        TRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<TRequest>(jsonArguments, WorkSessionToolSerialization.Options);
        }
        catch (JsonException exception)
        {
            // The parser's own message is for an operator, not for the model: it names the CLR request type and, for an
            // unknown property, never mentions the property the model should have used instead. It goes to the log; the
            // model gets a shape it can copy.
            Logger.LogDebug(exception, "{ToolName} could not read its arguments.", ToolName);
            return InvalidArguments;
        }

        if (request is null)
        {
            return $"{ToolName} arguments were empty.";
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();

        // Two writers touch one session per step by design: the supervisor moves the status while this handler writes
        // from inside the invocation loop. A lost race is expected, not a defect — re-read and try once more, then say
        // so in a sentence the model can act on.
        // One retry, no more: a second lost race means something other than the expected two-writer contention.
        const int MaxAttempts = 2;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var session = await store.FindByConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                return NotInWorkSession;
            }

            if (session.Status is AgentWorkSessionStatus.Completed or AgentWorkSessionStatus.Cancelled or AgentWorkSessionStatus.Failed)
            {
                return SessionClosed;
            }

            // Argument bounds are checked AFTER the session guards, not before: "this tool only works inside a work
            // session" is the more useful answer to a call that is both out of scope and malformed, and it is the answer
            // that does not leak the shape of a tool the caller cannot use anyway.
            if (Validate(request) is { } validationError)
            {
                return validationError;
            }

            WorkSessionToolOutcome outcome;
            try
            {
                outcome = await ExecuteCoreAsync(request, session, store, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkSessionConcurrencyException) when (attempt < MaxAttempts - 1)
            {
                continue;
            }
            catch (WorkSessionConcurrencyException)
            {
                return ToolName + ConcurrencyRefusalSuffix;
            }
            catch (KeyNotFoundException exception)
            {
                return $"{ToolName} referenced something this work session does not have: {exception.Message}";
            }

            if (outcome.Sequence is { } sequence)
            {
                await _publisher.PublishAsync(session.Id, sequence, outcome.Kind, cancellationToken).ConfigureAwait(false);
            }

            return outcome.Message;
        }

        return ToolName + ConcurrencyRefusalSuffix;
    }

    /// <summary>Argument bounds, checked before any scope or store is touched. Returns the sentence to hand back, or null.</summary>
    protected abstract string? Validate(TRequest request);

    protected abstract Task<WorkSessionToolOutcome> ExecuteCoreAsync(TRequest request,
        AgentWorkSessionSnapshot session,
        IAgentWorkSessionStore store,
        CancellationToken cancellationToken);

    /// <summary>The sentence handed back when the arguments would not read as this tool's shape.</summary>
    protected string InvalidArguments =>
        $"{ToolName} arguments were not valid JSON for this tool. Send exactly this shape and no other keys: {ExampleArguments}";

    protected string Exceeded(string argumentName, int maximumLength) =>
        $"{ToolName} argument '{argumentName}' exceeded the maximum length of {maximumLength} characters.";

    protected static bool Exceeds(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length > maximumLength;

    /// <summary>
    ///     Parses a model-supplied id. The four tools take ids as strings because a work-session state block hands them
    ///     out as text, and a malformed one has to read back as a sentence rather than a schema violation.
    /// </summary>
    protected static bool TryParseId(string? value, out Guid id)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            id = Guid.Empty;
            return false;
        }

        return Guid.TryParse(value, out id);
    }
}
