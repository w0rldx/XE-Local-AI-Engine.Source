namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Application-layer orchestration over <see cref="IAgentSkillStore" />: validates the supplied fields and delegates
///     persistence. The store owns id/version/timestamp stamping and the content-affecting version-bump rule; this
///     service never re-implements versioning. Validation rejects a blank or non-MAF-safe Name, a duplicate Name
///     (case-insensitive), a blank Description or Body, and any field over its length cap. The decrypted
///     Description/Body never enter a log or an exception message.
/// </summary>
public interface IAgentSkillService
{
    /// <summary>Validates and persists a new skill, returning the stored record (free text decrypted).</summary>
    Task<AgentSkillRecord> CreateAsync(AgentSkillInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and applies <paramref name="input" /> to the skill with <paramref name="id" />. Returns the updated
    ///     record, or <c>null</c> when no skill has that id.
    /// </summary>
    Task<AgentSkillRecord?> UpdateAsync(Guid id, AgentSkillInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the skill with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no skill has that id.</summary>
    Task<AgentSkillRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every skill in the library, ordered by Name (Ordinal).</summary>
    Task<IReadOnlyList<AgentSkillRecord>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a skill create/update fails validation. The message is safe to surface to callers (no skill body/description).</summary>
public sealed class AgentSkillValidationException : Exception
{
    public AgentSkillValidationException(string message) : base(message)
    {
    }

    public AgentSkillValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
