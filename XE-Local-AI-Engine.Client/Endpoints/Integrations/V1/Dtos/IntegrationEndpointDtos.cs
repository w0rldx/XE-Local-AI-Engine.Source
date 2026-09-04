namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Wire projection of one integration trigger.
///     <para>
///         <see cref="AcceptedInputKinds" /> crosses the wire as a <c>string[]</c> of member names
///         (<c>["text","json"]</c>) rather than the <c>[Flags]</c> enum's integer sum: a bitwise union is not
///         expressible in an OpenAPI enum, and the summed integer is unreadable in a generated SDK. The array is both.
///     </para>
/// </summary>
public sealed class IntegrationTriggerView
{
    public required Guid Id { get; init; }

    /// <summary>The external name a caller addresses. Lowercase by contract, and not editable after create.</summary>
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required bool Enabled { get; init; }

    public required IntegrationTargetKind TargetKind { get; init; }

    public required Guid TargetAgentDefinitionId { get; init; }

    public required IntegrationSessionPolicy SessionPolicy { get; init; }

    /// <summary>The accepted input kinds as member names, lowercased: <c>text</c> and/or <c>json</c>.</summary>
    public required IReadOnlyList<string> AcceptedInputKinds { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    /// <summary>The optimistic concurrency token an update must echo back.</summary>
    public required long Version { get; init; }
}

/// <summary>Response envelope for <c>GET integrations/triggers</c>.</summary>
public sealed class ListIntegrationTriggersResponse
{
    public required IReadOnlyList<IntegrationTriggerView> Items { get; init; }
}

/// <summary>Body of <c>POST integrations/triggers</c>.</summary>
public sealed class CreateIntegrationTriggerRequest
{
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public IntegrationTargetKind TargetKind { get; init; } = IntegrationTargetKind.Agent;

    public required Guid TargetAgentDefinitionId { get; init; }

    public IntegrationSessionPolicy SessionPolicy { get; init; } = IntegrationSessionPolicy.PerInvocation;

    /// <summary>At least one of <c>text</c> / <c>json</c>. An unknown member name is a validation failure.</summary>
    public required IReadOnlyList<string> AcceptedInputKinds { get; init; }
}

/// <summary>
///     Body of <c>PUT integrations/triggers/{triggerId}</c>. <c>Name</c> is absent on purpose: it is the external
///     contract a caller addresses, so renaming a live trigger is a delete-and-create decision rather than an edit.
/// </summary>
public sealed class UpdateIntegrationTriggerRequest
{
    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public required Guid TargetAgentDefinitionId { get; init; }

    public IntegrationSessionPolicy SessionPolicy { get; init; } = IntegrationSessionPolicy.PerInvocation;

    public required IReadOnlyList<string> AcceptedInputKinds { get; init; }

    /// <summary>The <c>Version</c> the caller read. A mismatch is a 409, never a silent overwrite.</summary>
    public required long ExpectedVersion { get; init; }
}

/// <summary>
///     Wire projection of one <c>xeint_</c> credential. Carries no secret by construction — the node keeps only a
///     digest — so it is safe on any Operator-gated surface.
/// </summary>
public sealed class IntegrationApiKeyView
{
    public required Guid Id { get; init; }

    /// <summary>
    ///     The integrator identity. Two keys sharing this value are one integrator, which is what makes a credential
    ///     rotation keep the sessions and in-flight executions the replaced key owned.
    /// </summary>
    public required Guid PrincipalId { get; init; }

    /// <summary>The non-secret display prefix (<c>xeint_</c> plus eight characters).</summary>
    public required string KeyPrefix { get; init; }

    public required string Label { get; init; }

    /// <summary><see langword="null" /> means the key may invoke EVERY trigger.</summary>
    public IReadOnlyList<Guid>? AllowedTriggerIds { get; init; }

    public required long CreatedAtUtc { get; init; }

    public long? LastUsedAtUtc { get; init; }

    /// <summary>Non-null on a revoked credential. The row survives revocation because execution rows reference its prefix.</summary>
    public long? RevokedAtUtc { get; init; }
}

/// <summary>Response envelope for <c>GET integrations/keys</c>.</summary>
public sealed class ListIntegrationApiKeysResponse
{
    public required IReadOnlyList<IntegrationApiKeyView> Items { get; init; }
}

/// <summary>Body of <c>POST integrations/keys</c>.</summary>
public sealed class GenerateIntegrationApiKeyRequest
{
    public required string Label { get; init; }

    /// <summary>Omit — or send <see langword="null" /> — to let the key invoke every trigger.</summary>
    public IReadOnlyList<Guid>? AllowedTriggerIds { get; init; }

    /// <summary>
    ///     Omit to mint a NEW integrator identity. Supplying an existing principal rotates or adds a credential for
    ///     that integrator, so the new key inherits everything the old one owned.
    /// </summary>
    public Guid? PrincipalId { get; init; }
}

/// <summary>
///     Response of <c>POST integrations/keys</c>. <see cref="Key" /> is the ONLY time the plaintext exists outside the
///     caller: every later read returns <see cref="View" /> alone.
/// </summary>
public sealed class GenerateIntegrationApiKeyResponse
{
    public required string Key { get; init; }

    public required IntegrationApiKeyView View { get; init; }
}

/// <summary>
///     One execution as the operator list renders it: what happened, to which trigger and session, and when. The
///     attribution and accounting columns live on <see cref="IntegrationExecutionDetailDto" /> instead, because a list
///     that carried them would ship a credential prefix into every table row for no rendering.
/// </summary>
public sealed class IntegrationExecutionSummaryDto
{
    public required Guid Id { get; init; }

    public required Guid TriggerId { get; init; }

    public required Guid SessionId { get; init; }

    public required IntegrationExecutionStatus Status { get; init; }

    public required long ReceivedAtUtc { get; init; }

    public long? StartedAtUtc { get; init; }

    public long? EndedAtUtc { get; init; }

    /// <summary>Non-null only on a failed row, and always one of the closed, content-free category constants.</summary>
    public string? FailureCategory { get; init; }

    /// <summary>A short operator-safe label. Never provider text and never any part of the caller's request.</summary>
    public string? FailureSummary { get; init; }

    /// <summary>How many <c>external.output</c> payloads the run committed. The row's own counter, never a buffer read.</summary>
    public required int OutputCount { get; init; }
}

/// <summary>
///     One execution in full: the summary plus the attribution and accounting an operator needs on a detail pane.
///     <see cref="PrincipalId" /> is the integrator identity; <see cref="KeyPrefix" /> only names which of that
///     integrator's credentials sent the request, and answers no ownership question.
/// </summary>
public sealed class IntegrationExecutionDetailDto
{
    public required IntegrationExecutionSummaryDto Execution { get; init; }

    public required Guid PrincipalId { get; init; }

    public required string KeyPrefix { get; init; }

    public required Guid RequestId { get; init; }

    /// <summary><see cref="Guid.Empty" /> until the run actually reaches the invocation runner.</summary>
    public required Guid InvocationId { get; init; }

    /// <summary>Plaintext UTF-8 bytes of the committed output payloads, so an operator can see a run approaching its cap.</summary>
    public required long OutputBytes { get; init; }

    /// <summary>The highest event sequence persisted for this execution.</summary>
    public required long LastSequence { get; init; }

    public required long Version { get; init; }

    /// <summary>Non-null once a cancel has been requested, whether or not the run had already started.</summary>
    public long? StopRequestedAtUtc { get; init; }
}

/// <summary>
///     Query for <c>GET integrations/executions</c>. Paging is SERVER-side through <see cref="Limit" /> and
///     <see cref="Offset" /> — the same two names the store's filter uses — so the history page can reach older rows
///     rather than slicing a bounded result client-side.
/// </summary>
public sealed class ListIntegrationExecutionsRequest
{
    public Guid? TriggerId { get; init; }

    public Guid? SessionId { get; init; }

    public IntegrationExecutionStatus? Status { get; init; }

    public int? Limit { get; init; }

    public int? Offset { get; init; }
}

/// <summary>Response envelope for <c>GET integrations/executions</c>, ordered newest first by the store.</summary>
public sealed class ListIntegrationExecutionsResponse
{
    public required IReadOnlyList<IntegrationExecutionSummaryDto> Items { get; init; }
}

/// <summary>
///     One persisted event on an execution's timeline. <b>One record, two routes:</b> the operator's
///     <c>GET integrations/executions/{id}/events</c> and the external
///     <c>GET integration-api/executions/{id}/events?sinceSeq=</c> return exactly this shape under two different
///     policies, so a caller recovering from a 410 and an operator reading the timeline see the same thing.
///     <para>
///         It holds the PERSISTED set only — the phase boundaries, the terminal, <c>tool.*</c> and the output events.
///         Neither assistant type is ever here: per-token deltas are stream-only, and the final text lives on the
///         owned conversation as an assistant message.
///     </para>
/// </summary>
public sealed class IntegrationExecutionEventDto
{
    public required Guid ExecutionId { get; init; }

    /// <summary>Ascending, but NOT contiguous: a durable write that failed leaves a permanent hole.</summary>
    public required long Sequence { get; init; }

    public required string EventType { get; init; }

    /// <summary>Already-decrypted JSON text, or null for the events that carry no payload.</summary>
    public string? DetailJson { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>
///     Query for <c>GET integrations/executions/{executionId}/events</c>. Paging is by WATERMARK, not by page number:
///     pass the last <c>sequence</c> you received back as <see cref="SinceSeq" />. A short page means "caught up"; a
///     full one means "call again". No offset exists, because holes make one meaningless.
/// </summary>
public sealed class ListIntegrationExecutionEventsRequest
{
    /// <summary>Exclusive, the same meaning <c>Last-Event-ID</c> has on the stream.</summary>
    public long? SinceSeq { get; init; }

    public int? Limit { get; init; }
}

/// <summary>Response envelope for <c>GET integrations/executions/{executionId}/events</c>, ascending by sequence.</summary>
public sealed class ListIntegrationExecutionEventsResponse
{
    public required IReadOnlyList<IntegrationExecutionEventDto> Items { get; init; }
}

/// <summary>
///     The one page bound both event routes share. It lives here rather than in either route because the operator's
///     timeline and the external recovery poll must page identically: a caller that learns the page size from one and
///     uses it against the other would otherwise silently skip rows.
/// </summary>
public static class IntegrationEventPage
{
    /// <summary>What a caller that names no limit gets.</summary>
    public const int DefaultLimit = 200;

    /// <summary>The ceiling. A bounded page is the point: an execution's timeline is unbounded in principle.</summary>
    public const int MaxLimit = 500;

    public static int ClampLimit(int? limit) =>
        Math.Clamp(limit ?? DefaultLimit, min: 1, MaxLimit);
}

/// <summary>
///     One caller-managed (or per-invocation) session as the operator surface renders it.
///     <para>
///         <see cref="TriggerName" /> rides along because it is the name an integrator addresses; an id alone would
///         make the row unreadable without a second lookup. <see cref="ExecutionCount" /> and
///         <see cref="LastActivityUtc" /> are written by the admission transaction and by every persisted event, so
///         they are the activity indicators a UI renders rather than values it computes.
///     </para>
/// </summary>
public sealed class IntegrationSessionResponse
{
    public required Guid Id { get; init; }

    public required Guid TriggerId { get; init; }

    /// <summary>Empty only when the trigger has since been deleted; the session and its executions outlive it.</summary>
    public required string TriggerName { get; init; }

    /// <summary>
    ///     The integrator that owns the session. Operator-only, like the execution detail's: it answers "whose is
    ///     this?" on an admin surface where every integrator's rows are visible at once. The external status route
    ///     does not carry it.
    /// </summary>
    public required Guid PrincipalId { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required IntegrationSessionStatus Status { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastActivityUtc { get; init; }

    public required int ExecutionCount { get; init; }
}

/// <summary>
///     Query for <c>GET integrations/sessions</c>. The same four names the executions list uses, and for the same
///     reason: paging a bounded result client-side would hide older sessions entirely.
/// </summary>
public sealed class ListIntegrationSessionsRequest
{
    public Guid? TriggerId { get; init; }

    public IntegrationSessionStatus? Status { get; init; }

    public int? Limit { get; init; }

    public int? Offset { get; init; }
}

/// <summary>
///     Response envelope for <c>GET integrations/sessions</c>, ordered <c>LastActivityUtc</c> then <c>Id</c> DESCENDING
///     by the store. That order is part of the contract: a client must render it rather than re-sort it, because a page
///     is a window onto a larger set and a locally sorted page would be labelled "latest" while missing later rows.
/// </summary>
public sealed class ListIntegrationSessionsResponse
{
    public required IReadOnlyList<IntegrationSessionResponse> Items { get; init; }
}
