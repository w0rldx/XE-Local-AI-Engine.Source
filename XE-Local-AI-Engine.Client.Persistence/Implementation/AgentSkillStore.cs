namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for the agent skill library.
/// </summary>
public sealed partial class AgentSkillStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IAgentSkillStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<AgentSkillRecord> CreateAsync(AgentSkillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateSourceUri(input.SourceUri);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new AgentSkill
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = Encoding.UTF8.GetBytes(input.Description),
            Body = Encoding.UTF8.GetBytes(input.Body),
            FrontmatterJson = Encode(SerializeFrontmatter(input)),
            Origin = (int)input.Origin,
            SourceUri = input.SourceUri,
            ImportedAtUtc = input.ImportedAtUtc,
            ContentSha256 = input.ContentSha256,
            Enabled = input.Enabled,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.AgentSkills.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity, resources: []);
    }

    public async Task<AgentSkillRecord?> UpdateAsync(Guid id, AgentSkillInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateSourceUri(input.SourceUri);

        // Load tracked (not AsNoTracking) so SaveChanges re-encrypts; the materialization interceptor has already
        // decrypted Description/Body/frontmatter on load, so the comparison below is plaintext-vs-plaintext.
        var entity = await _dbContext.AgentSkills
                                     .FirstOrDefaultAsync(skill => skill.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        // Name, Description, Body and the frontmatter are the content the model sees / loads, so they drive the config
        // hash and bump Version. The Enabled toggle only gates resolution (already covered by resolved-set membership
        // in the hash), so toggling it alone must NOT bump Version — mirrors the PlaybookAction/AgentDefinition version
        // rule. Provenance is not content either: re-stamping where a skill came from changes nothing the model reads.
        var frontmatterJson = SerializeFrontmatter(input);
        var configChanged = !string.Equals(entity.Name, input.Name, StringComparison.Ordinal)
                            || !string.Equals(Decode(entity.Description), input.Description, StringComparison.Ordinal)
                            || !string.Equals(Decode(entity.Body), input.Body, StringComparison.Ordinal)
                            || !string.Equals(DecodeIfPresent(entity.FrontmatterJson), frontmatterJson, StringComparison.Ordinal);

        entity.Name = input.Name;
        entity.Description = Encoding.UTF8.GetBytes(input.Description);
        entity.Body = Encoding.UTF8.GetBytes(input.Body);
        entity.FrontmatterJson = Encode(frontmatterJson);
        entity.Enabled = input.Enabled;

        // Provenance is promote-only, and absent values leave the stored provenance alone: an operator edit that did
        // not echo the import fields back must not launder an imported skill into a local one, because Origin is what
        // decides whether the body gets fenced as untrusted content downstream.
        if (input.Origin == AgentSkillOrigin.Imported)
        {
            entity.Origin = (int)AgentSkillOrigin.Imported;
        }

        entity.SourceUri = input.SourceUri ?? entity.SourceUri;
        entity.ImportedAtUtc = input.ImportedAtUtc ?? entity.ImportedAtUtc;
        entity.ContentSha256 = input.ContentSha256 ?? entity.ContentSha256;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (configChanged)
        {
            entity.Version++;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity, await LoadResourcesAsync(id, cancellationToken).ConfigureAwait(false));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.AgentSkills
                                     .FirstOrDefaultAsync(skill => skill.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.AgentSkills.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<AgentSkillRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.AgentSkills
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(skill => skill.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity, await LoadResourcesAsync(id, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<AgentSkillRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.AgentSkills
                                       .AsNoTracking()
                                       .OrderBy(skill => skill.Name)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        // The library list does not carry resources: decrypting every bundled file of every skill to render a list of
        // names would be pure waste. Callers that need them ask per skill.
        return entities.Select(entity => ToRecord(entity, resources: [])).ToArray();
    }

    public async Task<IReadOnlyList<AgentSkillRecord>> ListEnabledByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return [];
        }

        // Materialize the requested ids into a hash set so the EF Contains translates to a single IN (...) query and
        // duplicate ids in the picklist collapse. Filter to Enabled server-side; missing/disabled ids simply do not
        // appear in the result (the resolver drops + logs them).
        var idSet = ids.ToHashSet();

        var entities = await _dbContext.AgentSkills
                                       .AsNoTracking()
                                       .Where(skill => skill.Enabled && idSet.Contains(skill.Id))
                                       .OrderBy(skill => skill.Name)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        if (entities.Count == 0)
        {
            return [];
        }

        // One IN (...) query for every resolved skill's resources rather than one per skill: the resolver hands the
        // whole set to MAF at once, so a per-skill round trip would only add latency to the hot path.
        var resolvedIds = entities.Select(entity => entity.Id).ToHashSet();
        var resources = await _dbContext.AgentSkillResources
                                        .AsNoTracking()
                                        .Where(resource => resolvedIds.Contains(resource.SkillId))
                                        .OrderBy(resource => resource.Name)
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);

        var bySkill = resources.GroupBy(resource => resource.SkillId)
                               .ToDictionary(group => group.Key, group => (IReadOnlyList<AgentSkillResourceRecord>)group.Select(ToRecord).ToArray());

        return entities.Select(entity => ToRecord(entity, bySkill.TryGetValue(entity.Id, out var owned) ? owned : [])).ToArray();
    }

    public async Task<IReadOnlyList<AgentSkillResourceRecord>> ListResourcesAsync(Guid skillId, CancellationToken cancellationToken = default)
    {
        return await LoadResourcesAsync(skillId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSkillResourceRecord?> UpsertResourceAsync(Guid skillId, AgentSkillResourceInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var skill = await _dbContext.AgentSkills
                                    .FirstOrDefaultAsync(entity => entity.Id == skillId, cancellationToken)
                                    .ConfigureAwait(false);

        if (skill is null)
        {
            return null;
        }

        // The name column collates NOCASE, so this comparison is server-side case-insensitive and matches the unique
        // index. Any row it finds is removed rather than updated in place: the name is bound into the content AAD, so a
        // fresh row (fresh id, fresh seal) is the only way an edit stays readable.
        var superseded = await _dbContext.AgentSkillResources
                                         .Where(resource => resource.SkillId == skillId && resource.Name == input.Name)
                                         .ToListAsync(cancellationToken)
                                         .ConfigureAwait(false);

        _dbContext.AgentSkillResources.RemoveRange(superseded);

        var entity = CreateResource(skillId, input);
        _ = _dbContext.AgentSkillResources.Add(entity);
        BumpVersion(skill);

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<bool> DeleteResourceAsync(Guid skillId, Guid resourceId, CancellationToken cancellationToken = default)
    {
        var skill = await _dbContext.AgentSkills
                                    .FirstOrDefaultAsync(entity => entity.Id == skillId, cancellationToken)
                                    .ConfigureAwait(false);

        if (skill is null)
        {
            return false;
        }

        var entity = await _dbContext.AgentSkillResources
                                     .FirstOrDefaultAsync(resource => resource.Id == resourceId && resource.SkillId == skillId, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.AgentSkillResources.Remove(entity);
        BumpVersion(skill);

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<IReadOnlyList<AgentSkillResourceRecord>?> ReplaceResourcesAsync(Guid skillId,
        IReadOnlyList<AgentSkillResourceInput> resources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var skill = await _dbContext.AgentSkills
                                    .FirstOrDefaultAsync(entity => entity.Id == skillId, cancellationToken)
                                    .ConfigureAwait(false);

        if (skill is null)
        {
            return null;
        }

        var existing = await _dbContext.AgentSkillResources
                                       .Where(resource => resource.SkillId == skillId)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        _dbContext.AgentSkillResources.RemoveRange(existing);

        var replacements = resources.Select(resource => CreateResource(skillId, resource)).ToArray();
        _dbContext.AgentSkillResources.AddRange(replacements);
        BumpVersion(skill);

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return replacements.OrderBy(resource => resource.Name, StringComparer.Ordinal).Select(ToRecord).ToArray();
    }

    private async Task<IReadOnlyList<AgentSkillResourceRecord>> LoadResourcesAsync(Guid skillId, CancellationToken cancellationToken)
    {
        var entities = await _dbContext.AgentSkillResources
                                       .AsNoTracking()
                                       .Where(resource => resource.SkillId == skillId)
                                       .OrderBy(resource => resource.Name)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static AgentSkillResource CreateResource(Guid skillId, AgentSkillResourceInput input)
    {
        var content = Encoding.UTF8.GetBytes(input.Content);

        return new AgentSkillResource
        {
            Id = Guid.NewGuid(),
            SkillId = skillId,
            Name = input.Name,
            Description = input.Description,
            MediaType = input.MediaType,
            Content = content,
            SizeBytes = content.Length
        };
    }

    // A resource add, edit or removal is content-affecting: it changes what the model can fetch, so it has to move the
    // skill's Version and through it the runtime config hash, which is what invalidates a resumed run. Resource content
    // itself is deliberately NOT folded into the hash — the version bump already carries the whole invalidation.
    private void BumpVersion(AgentSkill skill)
    {
        skill.Version++;
        skill.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    private static AgentSkillRecord ToRecord(AgentSkill entity, IReadOnlyList<AgentSkillResourceRecord> resources)
    {
        var frontmatter = DeserializeFrontmatter(entity.FrontmatterJson);

        return new AgentSkillRecord(entity.Id,
            entity.Name,
            Decode(entity.Description),
            Decode(entity.Body),
            entity.Enabled,
            entity.Version,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            frontmatter?.License,
            frontmatter?.Compatibility,
            frontmatter?.AllowedTools,
            frontmatter?.Metadata,
            (AgentSkillOrigin)entity.Origin,
            entity.SourceUri,
            entity.ImportedAtUtc,
            entity.ContentSha256,
            resources);
    }

    private static AgentSkillResourceRecord ToRecord(AgentSkillResource entity)
    {
        return new AgentSkillResourceRecord(entity.Id,
            entity.SkillId,
            entity.Name,
            entity.Description,
            entity.MediaType,
            Decode(entity.Content),
            entity.SizeBytes);
    }

    // The four optional frontmatter fields share one encrypted column, so they are serialized and compared as a unit.
    // Metadata keys are sorted so the same frontmatter always produces the same bytes: without that, dictionary
    // ordering alone would look like a content edit and bump Version on every save.
    private static string? SerializeFrontmatter(AgentSkillInput input)
    {
        var metadata = input.Metadata is null || input.Metadata.Count == 0
            ? null
            : input.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        if (input.License is null && input.Compatibility is null && input.AllowedTools is null && metadata is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(new SkillFrontmatter(input.License, input.Compatibility, input.AllowedTools, metadata), SerializerOptions);
    }

    private static SkillFrontmatter? DeserializeFrontmatter(byte[]? frontmatterJson)
    {
        return frontmatterJson is null or { Length: 0 } ? null : JsonSerializer.Deserialize<SkillFrontmatter>(frontmatterJson, SerializerOptions);
    }

    // Uploads contribute their kind only. An operator-chosen archive filename would otherwise be the single
    // unencrypted free-text string in a table where the description, the body, the frontmatter and every resource are
    // AEAD-sealed — and it is plaintext precisely because provenance has to be greppable in logs and rendered in the
    // approval card, which is the wrong place for a filename off the operator's disk.
    private static void ValidateSourceUri(string? sourceUri)
    {
        if (sourceUri is null)
        {
            return;
        }

        if (!SourceUriPattern().IsMatch(sourceUri))
        {
            throw new ArgumentException($"Source URI '{sourceUri}' is not a supported provenance value; expected 'upload' or 'github:owner/repo'.", nameof(sourceUri));
        }
    }

    private static byte[]? Encode(string? value)
    {
        return value is null ? null : Encoding.UTF8.GetBytes(value);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }

    private static string? DecodeIfPresent(byte[]? value)
    {
        return value is null ? null : Encoding.UTF8.GetString(value);
    }

    [GeneratedRegex("^(?:upload|github:[A-Za-z0-9][A-Za-z0-9._-]{0,99}/[A-Za-z0-9][A-Za-z0-9._-]{0,99})$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SourceUriPattern();

    /// <summary>Wire shape of the <c>frontmatter_json</c> column — the spec's optional frontmatter fields, all nullable.</summary>
    private sealed record SkillFrontmatter(string? License, string? Compatibility, string? AllowedTools, IReadOnlyDictionary<string, string>? Metadata);
}
