namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for the user-defined custom tool library.
/// </summary>
public sealed class CustomToolStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : ICustomToolStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<CustomToolRecord> CreateAsync(CustomToolInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new CustomTool
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = Encoding.UTF8.GetBytes(input.Description),
            Kind = (int)input.Kind,
            Mode = (int)input.Mode,
            ParametersJson = input.ParametersJson,
            ConfigJson = Encoding.UTF8.GetBytes(input.ConfigJson),
            Enabled = input.Enabled,
            Acknowledged = input.Acknowledged,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.CustomTools.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<CustomToolRecord?> UpdateAsync(Guid id, CustomToolInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Load tracked (not AsNoTracking) so SaveChanges re-encrypts; the materialization interceptor has already
        // decrypted Description/ConfigJson on load, so the comparison below is plaintext-vs-plaintext.
        var entity = await _dbContext.CustomTools
                                     .FirstOrDefaultAsync(tool => tool.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        // Name, Description, Kind, Mode, the declared parameters and the kind-specific config are the surface the model
        // sees or the tool runs on, so they drive the config hash and bump Version. The Enabled toggle only gates the
        // offered set (already covered by membership in the hash) and Acknowledged is an authoring gate, not content —
        // toggling either alone must NOT bump Version, mirroring the AgentDefinition/AgentSkill version rule.
        var configChanged = !string.Equals(entity.Name, input.Name, StringComparison.Ordinal)
                            || !string.Equals(Decode(entity.Description), input.Description, StringComparison.Ordinal)
                            || entity.Kind != (int)input.Kind
                            || entity.Mode != (int)input.Mode
                            || !string.Equals(entity.ParametersJson, input.ParametersJson, StringComparison.Ordinal)
                            || !string.Equals(Decode(entity.ConfigJson), input.ConfigJson, StringComparison.Ordinal);

        entity.Name = input.Name;
        entity.Description = Encoding.UTF8.GetBytes(input.Description);
        entity.Kind = (int)input.Kind;
        entity.Mode = (int)input.Mode;
        entity.ParametersJson = input.ParametersJson;
        entity.ConfigJson = Encoding.UTF8.GetBytes(input.ConfigJson);
        entity.Enabled = input.Enabled;
        entity.Acknowledged = input.Acknowledged;
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
        var entity = await _dbContext.CustomTools
                                     .FirstOrDefaultAsync(tool => tool.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.CustomTools.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<CustomToolRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CustomTools
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(tool => tool.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<CustomToolRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.CustomTools
                                       .AsNoTracking()
                                       .OrderBy(tool => tool.Name)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static CustomToolRecord ToRecord(CustomTool entity)
    {
        return new CustomToolRecord(entity.Id,
            entity.Name,
            Decode(entity.Description),
            (CustomToolKind)entity.Kind,
            (CustomToolMode)entity.Mode,
            entity.ParametersJson,
            Decode(entity.ConfigJson),
            entity.Enabled,
            entity.Acknowledged,
            entity.Version,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }
}
