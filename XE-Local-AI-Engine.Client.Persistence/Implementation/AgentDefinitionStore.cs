namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for agent definition data.
/// </summary>
public sealed class AgentDefinitionStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IAgentDefinitionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<AgentDefinitionRecord> AddAsync(AgentDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = EncodeOptional(input.Description),
            Instructions = Encoding.UTF8.GetBytes(input.Instructions),
            ModelProfile = input.ModelProfile,
            ReasoningEffort = input.ReasoningEffort,
            Kind = (int)input.Kind,
            AllowedToolNamesJson = SerializeToolNames(input.AllowedToolNames),
            AllowedSkillIdsJson = SerializeSkillIds(input.AllowedSkillIds),
            ToolApprovalsJson = SerializeApprovals(input.ToolApprovals),
            OrchestrationTopologyJson = input.OrchestrationTopologyJson,
            PlaybookEnabled = input.PlaybookEnabled,
            DisableBaseScaffold = input.DisableBaseScaffold,
            DisableToolRelevanceFilter = input.DisableToolRelevanceFilter,
            DefaultTemporaryChat = input.DefaultTemporaryChat,
            MemoryExtractionEnabled = input.MemoryExtractionEnabled,
            GenerationMetadataJson = EncodeOptional(input.GenerationMetadataJson),
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.AgentDefinitions.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<AgentDefinitionRecord> AddSeededAsync(AgentDefinitionInput input, string seedSlug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(seedSlug);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = EncodeOptional(input.Description),
            Instructions = Encoding.UTF8.GetBytes(input.Instructions),
            ModelProfile = input.ModelProfile,
            ReasoningEffort = input.ReasoningEffort,
            Kind = (int)input.Kind,
            AllowedToolNamesJson = SerializeToolNames(input.AllowedToolNames),
            AllowedSkillIdsJson = SerializeSkillIds(input.AllowedSkillIds),
            ToolApprovalsJson = SerializeApprovals(input.ToolApprovals),
            OrchestrationTopologyJson = input.OrchestrationTopologyJson,
            PlaybookEnabled = input.PlaybookEnabled,
            DisableBaseScaffold = input.DisableBaseScaffold,
            DisableToolRelevanceFilter = input.DisableToolRelevanceFilter,
            DefaultTemporaryChat = input.DefaultTemporaryChat,
            MemoryExtractionEnabled = input.MemoryExtractionEnabled,
            // The only place a seeded provenance is stamped — the manual AddAsync leaves Source at the entity default
            // (Manual), keeping the operator create/update contract unable to forge a seeded row.
            Source = (int)AgentDefinitionSource.Seeded,
            SeedSlug = seedSlug,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.AgentDefinitions.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<AgentDefinitionRecord?> UpdateAsync(Guid id, AgentDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Load tracked (not AsNoTracking) so SaveChanges re-encrypts; the materialization interceptor has already
        // decrypted Instructions/Description on load, so the comparison below is plaintext-vs-plaintext.
        var entity = await _dbContext.AgentDefinitions
                                     .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        var allowedToolNamesJson = SerializeToolNames(input.AllowedToolNames);
        var allowedSkillIdsJson = SerializeSkillIds(input.AllowedSkillIds);
        var toolApprovalsJson = SerializeApprovals(input.ToolApprovals);

        // AllowedToolNames stays order-sensitive (order feeds the offer list and thus the config hash), but
        // ToolApprovals only reach the config hash via each tool's RequiresApproval in offer order, so a pure key
        // reorder must NOT count as a config change. Compare a key-sorted canonical projection on both sides.
        var approvalsChanged = !string.Equals(CanonicalizeApprovals(DeserializeApprovals(entity.ToolApprovalsJson)),
            CanonicalizeApprovals(input.ToolApprovals),
            StringComparison.Ordinal);

        var configChanged = !string.Equals(Decode(entity.Instructions), input.Instructions, StringComparison.Ordinal)
                            || !string.Equals(entity.ModelProfile, input.ModelProfile, StringComparison.Ordinal)
                            || !string.Equals(entity.ReasoningEffort, input.ReasoningEffort, StringComparison.Ordinal)
                            || entity.Kind != (int)input.Kind
                            || !string.Equals(entity.AllowedToolNamesJson, allowedToolNamesJson, StringComparison.Ordinal)
                            // Changing the assigned skill set is config-affecting for the agent — same class as the tool
                            // list (order-sensitive ordinal compare of the serialized id array).
                            || !string.Equals(entity.AllowedSkillIdsJson, allowedSkillIdsJson, StringComparison.Ordinal)
                            || approvalsChanged
                            || !string.Equals(entity.OrchestrationTopologyJson, input.OrchestrationTopologyJson, StringComparison.Ordinal);

        entity.Name = input.Name;
        entity.Description = EncodeOptional(input.Description);
        entity.Instructions = Encoding.UTF8.GetBytes(input.Instructions);
        entity.ModelProfile = input.ModelProfile;
        entity.ReasoningEffort = input.ReasoningEffort;
        entity.Kind = (int)input.Kind;
        entity.AllowedToolNamesJson = allowedToolNamesJson;
        entity.AllowedSkillIdsJson = allowedSkillIdsJson;
        entity.ToolApprovalsJson = toolApprovalsJson;
        entity.OrchestrationTopologyJson = input.OrchestrationTopologyJson;
        // PlaybookEnabled only gates injection; the injected playbook content drives the config hash directly, so it is
        // deliberately excluded from the configChanged comparison above and never bumps the agent's own Version.
        entity.PlaybookEnabled = input.PlaybookEnabled;
        // DisableBaseScaffold toggles whether the scaffold is folded into the resolved prompt; like PlaybookEnabled the
        // prompt change drives the config hash directly, so it is excluded from configChanged and never bumps Version.
        entity.DisableBaseScaffold = input.DisableBaseScaffold;
        // DisableToolRelevanceFilter narrows only the provider-bound tools array — never the offer, the prompt or the
        // config hash — so like the two above it is excluded from configChanged and never bumps Version.
        entity.DisableToolRelevanceFilter = input.DisableToolRelevanceFilter;
        // DefaultTemporaryChat gates post-run memory extraction only (not the prompt), so like PlaybookEnabled it is
        // excluded from configChanged and never bumps Version.
        entity.DefaultTemporaryChat = input.DefaultTemporaryChat;
        // MemoryExtractionEnabled gates post-run extraction only (retrieval/injection stays gated on PlaybookEnabled),
        // so like PlaybookEnabled it is excluded from configChanged and never bumps Version.
        entity.MemoryExtractionEnabled = input.MemoryExtractionEnabled;
        // Set-if-present: the AI provenance block only travels with a save that came out of the assist dialog, so an
        // ordinary edit omitting it must leave the stored record intact rather than clear it. Not config-affecting —
        // deliberately absent from configChanged above, so it never bumps Version.
        entity.GenerationMetadataJson = EncodeOptional(input.GenerationMetadataJson) ?? entity.GenerationMetadataJson;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (configChanged)
        {
            entity.Version++;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.AgentDefinitions
                                     .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.AgentDefinitions.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<AgentDefinitionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.AgentDefinitions
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<AgentDefinitionRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.AgentDefinitions
                                       .AsNoTracking()
                                       .OrderBy(definition => definition.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlySet<string>> ListSeededSlugsAsync(CancellationToken cancellationToken = default)
    {
        // Project the slug column only — never load the encrypted Instructions/Description, since this is just the
        // idempotency check before an import.
        var slugs = await _dbContext.AgentDefinitions
                                    .AsNoTracking()
                                    .Where(definition => definition.Source == (int)AgentDefinitionSource.Seeded && definition.SeedSlug != null)
                                    .Select(definition => definition.SeedSlug!)
                                    .ToListAsync(cancellationToken)
                                    .ConfigureAwait(false);

        return slugs.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<AgentDefinitionRecord?> GetBySeedSlugAsync(string seedSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedSlug);

        var entity = await _dbContext.AgentDefinitions
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(definition => definition.Source == (int)AgentDefinitionSource.Seeded && definition.SeedSlug == seedSlug, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    private static AgentDefinitionRecord ToRecord(AgentDefinition entity)
    {
        return new AgentDefinitionRecord(entity.Id,
            entity.Name,
            entity.Description is null ? null : Decode(entity.Description),
            Decode(entity.Instructions),
            entity.ModelProfile,
            entity.ReasoningEffort,
            (AgentDefinitionKind)entity.Kind,
            DeserializeToolNames(entity.AllowedToolNamesJson),
            DeserializeApprovals(entity.ToolApprovalsJson),
            entity.OrchestrationTopologyJson,
            entity.Version,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.PlaybookEnabled,
            (AgentDefinitionSource)entity.Source,
            entity.SeedSlug,
            DeserializeSkillIds(entity.AllowedSkillIdsJson),
            entity.DefaultTemporaryChat,
            entity.MemoryExtractionEnabled,
            entity.DisableBaseScaffold,
            entity.GenerationMetadataJson is null ? null : Decode(entity.GenerationMetadataJson),
            entity.DisableToolRelevanceFilter);
    }

    private static byte[]? EncodeOptional(string? value)
    {
        return value is null ? null : Encoding.UTF8.GetBytes(value);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }

    private static string SerializeToolNames(IReadOnlyList<string> toolNames)
    {
        return JsonSerializer.Serialize(toolNames, SerializerOptions);
    }

    private static string SerializeSkillIds(IReadOnlyList<Guid>? skillIds)
    {
        // Null (no assignment supplied) serializes to '[]' so a pre-skills caller persists an empty picklist, matching
        // the column default/backfill.
        return JsonSerializer.Serialize(skillIds ?? [], SerializerOptions);
    }

    private static string SerializeApprovals(IReadOnlyDictionary<string, bool> approvals)
    {
        return JsonSerializer.Serialize(approvals, SerializerOptions);
    }

    /// <summary>
    ///     Order-insensitive serialization of an approvals map used only for change detection: a key reorder of the
    ///     same approvals yields identical output, so it does not spuriously bump <c>Version</c>.
    /// </summary>
    private static string CanonicalizeApprovals(IReadOnlyDictionary<string, bool> approvals)
    {
        var sorted = approvals.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                              .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(sorted, SerializerOptions);
    }

    private static IReadOnlyList<string> DeserializeToolNames(string json)
    {
        return JsonSerializer.Deserialize<List<string>>(json, SerializerOptions) ?? [];
    }

    private static IReadOnlyList<Guid> DeserializeSkillIds(string json)
    {
        return JsonSerializer.Deserialize<List<Guid>>(json, SerializerOptions) ?? [];
    }

    private static IReadOnlyDictionary<string, bool> DeserializeApprovals(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, bool>>(json, SerializerOptions) ?? new Dictionary<string, bool>(StringComparer.Ordinal);
    }
}
