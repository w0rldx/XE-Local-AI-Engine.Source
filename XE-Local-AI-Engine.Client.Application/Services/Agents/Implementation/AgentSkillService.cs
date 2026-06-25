namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Validates skill content (MAF-safe Name, NOCASE-unique Name, non-blank Description/Body, length caps) and delegates
///     persistence to <see cref="IAgentSkillStore" />. The store stamps id/version/timestamp and owns the version-bump
///     rule; this service never touches versioning. A validation failure throws <see cref="AgentSkillValidationException" />
///     whose message is safe to surface (it never echoes the skill Description or Body).
/// </summary>
internal sealed partial class AgentSkillService : IAgentSkillService
{
    // MAF skill-name shape: lowercase letters/digits and internal dashes, never starting or ending with a dash. This
    // matches the kebab-case AgentInlineSkill name requirement so a stored skill always builds into a valid MAF skill.
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    // Matches the AgentDefinition.Instructions cap so a skill body cannot exceed the per-agent instruction budget.
    private const int MaxBodyLength = 20000;

    private readonly IAgentSkillStore _store;

    public AgentSkillService(IAgentSkillStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AgentSkillRecord> CreateAsync(AgentSkillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, existingId: null, cancellationToken).ConfigureAwait(false);

        return await _store.CreateAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSkillRecord?> UpdateAsync(Guid id, AgentSkillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, id, cancellationToken).ConfigureAwait(false);

        return await _store.UpdateAsync(id, input, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.DeleteAsync(id, cancellationToken);
    }

    public Task<AgentSkillRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.GetByIdAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<AgentSkillRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _store.ListAsync(cancellationToken);
    }

    private async Task ValidateAsync(AgentSkillInput input, Guid? existingId, CancellationToken cancellationToken)
    {
        // The Name is validated as supplied (never trimmed): it is the MAF skill identifier persisted verbatim, and the
        // regex below rejects surrounding/embedded whitespace, so a name that needed trimming is invalid by definition.
        var name = input.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AgentSkillValidationException("Name is required.");
        }

        if (name.Length > MaxNameLength)
        {
            throw new AgentSkillValidationException($"Name must be at most {MaxNameLength} characters.");
        }

        if (!SkillNameRegex().IsMatch(name))
        {
            throw new AgentSkillValidationException("Name must be lowercase letters, digits, and dashes, and may not start or end with a dash.");
        }

        if (string.IsNullOrWhiteSpace(input.Description))
        {
            throw new AgentSkillValidationException("Description is required.");
        }

        if (input.Description.Length > MaxDescriptionLength)
        {
            throw new AgentSkillValidationException($"Description must be at most {MaxDescriptionLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(input.Body))
        {
            throw new AgentSkillValidationException("Body is required.");
        }

        if (input.Body.Length > MaxBodyLength)
        {
            throw new AgentSkillValidationException($"Body must be at most {MaxBodyLength} characters.");
        }

        await EnsureNameIsUniqueAsync(name, existingId, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureNameIsUniqueAsync(string name, Guid? existingId, CancellationToken cancellationToken)
    {
        // NOCASE uniqueness: the persistence index is case-insensitive, so a duplicate name (any casing) other than
        // this skill itself is rejected up front rather than surfacing a downstream unique-constraint failure.
        var existing = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var clash = existing.Any(skill =>
            (existingId is null || skill.Id != existingId.Value)
            && string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase));

        if (clash)
        {
            throw new AgentSkillValidationException($"A skill named '{name}' already exists.");
        }
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SkillNameRegex();
}
