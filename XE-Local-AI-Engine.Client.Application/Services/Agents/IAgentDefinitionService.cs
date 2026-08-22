namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Application-layer orchestration over <see cref="IAgentDefinitionStore" />: validates the supplied fields and
///     delegates persistence. The store owns id/version/timestamp stamping and the config-affecting version-bump rule;
///     this service never re-implements versioning. Validation rejects empty Name/Instructions and approval keys that
///     fall outside the allowed-tool set, and warns (without failing) when a tool name is not in the node catalog so an
///     uninstalled tool can be re-enabled later.
/// </summary>
public interface IAgentDefinitionService
{
    /// <summary>Validates and persists a new definition, returning the stored record.</summary>
    Task<AgentDefinitionRecord> CreateAsync(AgentDefinitionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and applies <paramref name="input" /> to the definition with <paramref name="id" />. Returns the
    ///     updated record, or <c>null</c> when no definition has that id.
    /// </summary>
    Task<AgentDefinitionRecord?> UpdateAsync(Guid id, AgentDefinitionInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the definition with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no definition has that id.</summary>
    Task<AgentDefinitionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves a definition key as either its id or an exact, ordinal name. Blank keys never match.
    /// </summary>
    Task<AgentDefinitionRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Returns every registered definition, oldest first.</summary>
    Task<IReadOnlyList<AgentDefinitionRecord>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Thrown when an agent-definition create/update fails validation. The message is safe to surface to callers.</summary>
public sealed class AgentDefinitionValidationException : Exception
{
    public AgentDefinitionValidationException(string message) : base(message)
    {
    }

    public AgentDefinitionValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
