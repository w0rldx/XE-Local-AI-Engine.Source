namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Application-layer orchestration over <see cref="IPlaybookActionStore" />: validates the supplied fields and
///     delegates persistence. The store owns id/version/timestamp stamping and the config-affecting version-bump rule;
///     this service never re-implements versioning. Validation rejects a blank Behavior, an unknown owning agent, and
///     the lifecycle/provenance states reserved for later phases (P1 accepts only <c>Enabled</c>/<c>Disabled</c> and
///     forces <c>Source = Manual</c>).
/// </summary>
public interface IPlaybookActionService
{
    /// <summary>Validates and persists a new playbook action, returning the stored record.</summary>
    Task<PlaybookActionRecord> CreateAsync(PlaybookActionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and applies <paramref name="input" /> to the action with <paramref name="id" />. Returns the
    ///     updated record, or <c>null</c> when no action has that id <b>or</b> when the action belongs to a different
    ///     agent than the one named on <paramref name="input" /> (<c>AgentDefinitionId</c>). The ownership check stops a
    ///     nested-route IDOR — one agent's playbook route may not update or re-parent another agent's action.
    /// </summary>
    Task<PlaybookActionRecord?> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the action with <paramref name="id" /> only when it belongs to <paramref name="agentDefinitionId" />
    ///     (the agent named on the route). Returns <c>true</c> when a row was deleted, <c>false</c> when no action has
    ///     that id or it belongs to a different agent — the same ownership guard as <see cref="UpdateAsync" />.
    /// </summary>
    Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no action has that id.</summary>
    Task<PlaybookActionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every action for <paramref name="agentDefinitionId" />, ordered by Priority then CreatedAtUtc.</summary>
    Task<IReadOnlyList<PlaybookActionRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a playbook-action create/update fails validation. The message is safe to surface to callers.</summary>
public sealed class PlaybookActionValidationException : Exception
{
    public PlaybookActionValidationException(string message) : base(message)
    {
    }

    public PlaybookActionValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
