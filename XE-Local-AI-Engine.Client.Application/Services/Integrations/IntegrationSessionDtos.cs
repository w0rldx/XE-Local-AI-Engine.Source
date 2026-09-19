namespace XE_Local_AI_Engine.Client.Services.Integrations;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One session as both surfaces render it: the operator's admin list and detail, and the integrator's own
///     principal-scoped status read. <see cref="TriggerName" /> rides along because it is the name the integrator
///     addresses — an id would make the external body unusable without a second lookup the caller cannot make.
///     <para>
///         <see cref="PrincipalId" /> is the OWNING INTEGRATOR, and it is projected onto the operator surface only.
///         The external status route renders its own <c>IntegrationSessionStatusResponse</c> and deliberately does not
///         carry it: a caller already knows which principal it authenticated as, and the masking rules on that route
///         answer every foreign session identically.
///     </para>
/// </summary>
public sealed class IntegrationSessionDto
{
    public required Guid Id { get; init; }

    public required Guid TriggerId { get; init; }

    public required string TriggerName { get; init; }

    public required Guid PrincipalId { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required IntegrationSessionStatus Status { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastActivityUtc { get; init; }

    public required int ExecutionCount { get; init; }
}

/// <summary>
///     The admin list query. Filtering and paging are SERVER-side: a client-side page over a bounded result would hide
///     older sessions entirely, and the store's <c>LastActivityUtc DESC, Id DESC</c> order is part of the contract the
///     UI renders rather than re-sorts.
/// </summary>
public sealed class IntegrationSessionFilter
{
    public required Guid? TriggerId { get; init; }

    public required IntegrationSessionStatus? Status { get; init; }

    public required int Limit { get; init; }

    public required int Offset { get; init; }
}

/// <summary>What an operator delete decided. <see cref="Busy" /> is the 409 with a cancel-first message.</summary>
public enum IntegrationSessionDeleteOutcome
{
    Deleted,
    NotFound,
    Busy
}

/// <summary>
///     What the invocation gate decided about a caller's <c>sessionId</c>, and — on
///     <see cref="IntegrationAcceptOutcome.Accepted" /> for a continuation — the session the accept path must write
///     into. <see cref="Existing" /> is <see langword="null" /> for a fresh session, which is exactly the shape
///     <c>IntegrationAcceptCommand.NewSession</c> keys on.
///     <para>
///         It carries a public <see cref="IntegrationSessionSnapshot" />, never the internal entity: entities stay
///         inside the persistence assembly and only records cross a store boundary.
///     </para>
/// </summary>
internal sealed class IntegrationSessionGateResult
{
    public required IntegrationAcceptOutcome Outcome { get; init; }

    public required IntegrationSessionSnapshot? Existing { get; init; }

    public required string Message { get; init; }
}
