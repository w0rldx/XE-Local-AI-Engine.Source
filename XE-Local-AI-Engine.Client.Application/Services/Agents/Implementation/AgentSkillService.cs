namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.Agents.AI;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Validates skill content — MAF-safe and NOCASE-unique Name, non-blank Description and Body, length caps — and
///     delegates persistence to <see cref="IAgentSkillStore" />.
/// </summary>
/// <remarks>
///     The store stamps id, version and timestamp and owns the version-bump rule; this service never touches
///     versioning, and its <see cref="AgentSkillValidationException" /> messages never echo the Description or Body.
///     Name and Description are validated by <see cref="AgentSkillFrontmatter" /> itself, the very code MAF runs when
///     building an <c>AgentInlineSkill</c>, never by a local regex: a local one drifted from the specification and
///     persisted names MAF then rejected at agent-construction time, breaking every agent the skill was assigned to.
/// </remarks>
internal sealed class AgentSkillService : IAgentSkillService
{
    // Matches the AgentDefinition.Instructions cap so a skill body cannot exceed the per-agent instruction budget.
    // MAF has no body cap of its own, so this one stays local.
    private const int MaxBodyLength = 20000;

    // Optional frontmatter caps: MAF validates only Name and Description, so the other four would reach the store
    // unbounded from imported SKILL.md. Compatibility mirrors AgentSkillFrontmatter.MaxCompatibilityLength exactly.
    private const int MaxLicenseLength = 200;
    private const int MaxCompatibilityLength = 500;
    private const int MaxAllowedToolsLength = 1024;
    private const int MaxMetadataEntries = 32;
    private const int MaxMetadataKeyLength = 64;
    private const int MaxMetadataValueLength = 512;

    private readonly IAgentSkillStore _store;

    public AgentSkillService(IAgentSkillStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AgentSkillRecord> CreateAsync(AgentSkillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, existingId: null, cancellationToken);

        return await _store.CreateAsync(input, cancellationToken);
    }

    public async Task<AgentSkillRecord?> UpdateAsync(Guid id, AgentSkillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, id, cancellationToken);

        return await _store.UpdateAsync(id, input, cancellationToken);
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

        // MAAI001: Agent Skills stay [Experimental] at the version pinned in Directory.Packages.props. These validators
        // are the ones the AgentInlineSkill constructor runs, and their messages name the rule, echoing no content.
#pragma warning disable MAAI001
        if (!AgentSkillFrontmatter.ValidateName(name, out var nameError))
        {
            throw new AgentSkillValidationException(nameError);
        }
#pragma warning restore MAAI001

        if (string.IsNullOrWhiteSpace(input.Description))
        {
            throw new AgentSkillValidationException("Description is required.");
        }

#pragma warning disable MAAI001 // Same [Experimental] Agent Skills validators as the scoped block above.
        if (!AgentSkillFrontmatter.ValidateDescription(input.Description, out var descriptionError))
        {
            throw new AgentSkillValidationException(descriptionError);
        }
#pragma warning restore MAAI001

        if (string.IsNullOrWhiteSpace(input.Body))
        {
            throw new AgentSkillValidationException("Body is required.");
        }

        if (input.Body.Length > MaxBodyLength)
        {
            throw new AgentSkillValidationException($"Body must be at most {MaxBodyLength} characters.");
        }

        ValidateFrontmatter(input);

        await EnsureNameIsUniqueAsync(name, existingId, cancellationToken);
    }

    /// <summary>
    ///     Bounds the optional Agent Skills frontmatter, which MAF does not validate.
    /// </summary>
    /// <remarks>
    ///     These four fields reach this service from imported SKILL.md files as well as the editor, so an unbounded
    ///     value would be encrypted and persisted verbatim. Messages name the field and the limit only, never the
    ///     rejected value, which for an imported skill is attacker-authored text.
    /// </remarks>
    private static void ValidateFrontmatter(AgentSkillInput input)
    {
        if (input.License is { Length: > MaxLicenseLength })
        {
            throw new AgentSkillValidationException($"License must be at most {MaxLicenseLength} characters.");
        }

        if (input.Compatibility is { Length: > MaxCompatibilityLength })
        {
            throw new AgentSkillValidationException($"Compatibility must be at most {MaxCompatibilityLength} characters.");
        }

        if (input.AllowedTools is { Length: > MaxAllowedToolsLength })
        {
            throw new AgentSkillValidationException($"Allowed tools must be at most {MaxAllowedToolsLength} characters.");
        }

        if (input.Metadata is not { Count: > 0 } metadata)
        {
            return;
        }

        if (metadata.Count > MaxMetadataEntries)
        {
            throw new AgentSkillValidationException($"Metadata must contain at most {MaxMetadataEntries} entries.");
        }

        foreach (var pair in metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > MaxMetadataKeyLength)
            {
                throw new AgentSkillValidationException($"Each metadata key must be non-blank and at most {MaxMetadataKeyLength} characters.");
            }

            if (pair.Value is { Length: > MaxMetadataValueLength })
            {
                throw new AgentSkillValidationException($"Each metadata value must be at most {MaxMetadataValueLength} characters.");
            }
        }
    }

    private async Task EnsureNameIsUniqueAsync(string name, Guid? existingId, CancellationToken cancellationToken)
    {
        // NOCASE uniqueness: the persistence index is case-insensitive, so a duplicate name (any casing) other than
        // this skill itself is rejected up front rather than surfacing a downstream unique-constraint failure.
        var existing = await _store.ListAsync(cancellationToken);
        var clash = existing.Any(skill =>
            (existingId is null || skill.Id != existingId.Value)
            && string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase));

        if (clash)
        {
            throw new AgentSkillValidationException($"A skill named '{name}' already exists.");
        }
    }
}
