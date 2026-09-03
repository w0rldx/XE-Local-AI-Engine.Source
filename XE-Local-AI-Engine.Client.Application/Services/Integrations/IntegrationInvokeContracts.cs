namespace XE_Local_AI_Engine.Client.Services.Integrations;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One input from an invoke body, already parsed. <see cref="Kind" /> holds exactly one flag;
///     <see cref="Text" /> carries a text input's content and <see cref="Json" /> the RAW JSON TEXT of a json input —
///     raw text rather than a <c>JsonElement</c> so nothing downstream depends on a document the request owns.
/// </summary>
public sealed record IntegrationInputDto(IntegrationInputKinds Kind, string? Text, string? Label, string? Json);

/// <summary>
///     Everything the accept path needs. <see cref="RawBody" /> is the exact bytes the handler read off the wire,
///     carried through because the dedup fingerprint is over them: a retry that does not resend a byte-identical body
///     is a 409, deliberately, and there is no JSON canonicalisation anywhere in this feature.
///     <para>
///         <see cref="PrincipalId" /> is the IDENTITY — ownership, request uniqueness and the fingerprint all key on
///         it. <see cref="KeyPrefix" /> rides along only so the execution row and the audit row can name which
///         credential was used.
///     </para>
/// </summary>
public sealed record IntegrationAcceptRequest(string TriggerName,
    Guid PrincipalId,
    string KeyPrefix,
    Guid RequestId,
    Guid? SessionId,
    IReadOnlyList<IntegrationInputDto> Inputs,
    ReadOnlyMemory<byte> RawBody);

/// <summary>
///     What an accept decided. There is deliberately no <c>TriggerForbidden</c>: a key that is not allowlisted for the
///     trigger gets <see cref="IntegrationAcceptOutcome.TriggerNotFound" />, byte-identical to an unknown name, so a
///     narrow key cannot probe for names it is not scoped to.
/// </summary>
public enum IntegrationAcceptOutcome
{
    /// <summary>Admitted and durable. 202, or the head of a stream.</summary>
    Accepted,

    /// <summary>The same request id and the same body bytes as a live row: the existing execution is returned, with 202.</summary>
    Duplicate,

    /// <summary>Unknown name, a disabled trigger, or one this key is not allowlisted for — one 404 for all three.</summary>
    TriggerNotFound,

    /// <summary>The body's inputs are empty, of a kind the trigger does not accept, or compose past the seed ceiling. 422.</summary>
    InputsRejected,

    /// <summary>The same request id with DIFFERENT body bytes. 409, with no details.</summary>
    RequestConflict,

    /// <summary>The node-wide or per-principal admission cap was full. 503 with a <c>Retry-After</c>, and nothing written.</summary>
    QueueFull,

    /// <summary>Caller-managed sessions are not reachable yet. 422.</summary>
    SessionUnsupported,

    /// <summary>The credential was revoked between authentication and admission. The same generic 401 the auth handler writes.</summary>
    Unauthorized
}

/// <summary>
///     The accept's answer. <see cref="ExecutionId" />, <see cref="SessionId" /> and <see cref="Status" /> are populated
///     for <see cref="IntegrationAcceptOutcome.Accepted" /> and <see cref="IntegrationAcceptOutcome.Duplicate" /> and
///     are null otherwise — a rejection tells the caller nothing about rows it does not own.
/// </summary>
public sealed record IntegrationAcceptResult(IntegrationAcceptOutcome Outcome,
    Guid? ExecutionId,
    Guid? SessionId,
    IntegrationExecutionStatus? Status,
    string Message);

/// <summary>
///     The CLOSED failure vocabulary. Ten values and no more — an eleventh is a bug, not an extension point — and every
///     one is content-free by contract, so a category can be rendered in a UI and written to an audit row without
///     leaking any part of a caller's request.
/// </summary>
public static class IntegrationFailureCategories
{
    /// <summary>The trigger or its target agent was gone or unusable by the time the run started.</summary>
    public const string TriggerUnavailable = "trigger-unavailable";

    /// <summary>The effective model resolved to a cloud or remote provider, which unattended runs never use.</summary>
    public const string CloudModelRejected = "cloud-model-rejected";

    /// <summary>The capacity service refused the model.</summary>
    public const string CapacityRejected = "capacity-rejected";

    /// <summary>The process restarted while the row was non-terminal. V1 does not resume in-flight generations.</summary>
    public const string Restart = "restart";

    /// <summary>The queue could not take the admitted row. Defended, not expected: the admission count gates it.</summary>
    public const string QueueFull = "queue-full";

    /// <summary>The host is shutting down.</summary>
    public const string Shutdown = "shutdown";

    /// <summary>Anything the run itself reported as a failure, and every unexpected fault along the way.</summary>
    public const string InternalFailure = "internal-failure";

    /// <summary>An unattended run reached an approval-gated tool. The tool is NOT stripped, so this failure is loud and audited.</summary>
    public const string ApprovalRequired = "approval-required";

    /// <summary>The execution outlived <c>MaxQueueAgeSeconds</c> waiting for the invocation lease.</summary>
    public const string QueueTimeout = "queue-timeout";

    /// <summary>A caller-managed trigger resolved to an agent offering a tool outside <c>ToolCategory.ReadLocal</c>.</summary>
    public const string SessionPolicy = "session-policy";

    /// <summary>The whole vocabulary, so a test can assert nothing else is ever written.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        TriggerUnavailable,
        CloudModelRejected,
        CapacityRejected,
        Restart,
        QueueFull,
        Shutdown,
        InternalFailure,
        ApprovalRequired,
        QueueTimeout,
        SessionPolicy
    };
}
