namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for mcp server data.
/// </summary>
public sealed class McpServerStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IMcpServerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<McpServerRecord> AddAsync(McpServerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new McpServerRegistration
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = EncodeOptional(input.Description),
            TransportKind = (int)input.TransportKind,
            Command = input.Command,
            ArgumentsJson = EncodeArguments(input.Arguments),
            WorkingDirectory = input.WorkingDirectory,
            EnvJson = EncodeEnvironment(input.Environment),
            Url = input.Url,
            // A registration is always persisted disabled — enabling is a deliberate second action.
            Enabled = false,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.McpServers.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<McpServerRecord?> UpdateAsync(Guid id, McpServerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Load tracked (not AsNoTracking) so SaveChanges re-encrypts; the materialization interceptor has already
        // decrypted ArgumentsJson/EnvJson/Description on load, so the comparison below is plaintext-vs-plaintext.
        var entity = await _dbContext.McpServers
                                     .FirstOrDefaultAsync(server => server.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        var argumentsJson = EncodeArguments(input.Arguments);
        var environmentJson = EncodeEnvironment(input.Environment);

        // A change to anything that affects how the connection manager connects/launches the server bumps Version
        // (transport, command, args, env, url) — plus the enable/disable toggle, which changes the connected set. A
        // pure Name/Description edit does not. Compare decoded plaintext on both sides.
        var configChanged = entity.TransportKind != (int)input.TransportKind
                            || !string.Equals(entity.Command, input.Command, StringComparison.Ordinal)
                            || !ArgumentsEqual(DecodeArguments(entity.ArgumentsJson), input.Arguments)
                            || !string.Equals(entity.WorkingDirectory, input.WorkingDirectory, StringComparison.Ordinal)
                            || !EnvironmentEqual(DecodeEnvironment(entity.EnvJson), input.Environment)
                            || !string.Equals(entity.Url, input.Url, StringComparison.Ordinal)
                            || entity.Enabled != input.Enabled;

        entity.Name = input.Name;
        entity.Description = EncodeOptional(input.Description);
        entity.TransportKind = (int)input.TransportKind;
        entity.Command = input.Command;
        entity.ArgumentsJson = argumentsJson;
        entity.WorkingDirectory = input.WorkingDirectory;
        entity.EnvJson = environmentJson;
        entity.Url = input.Url;
        entity.Enabled = input.Enabled;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (configChanged)
        {
            entity.Version++;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<McpServerRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.McpServers
                                     .FirstOrDefaultAsync(server => server.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        // Only the enabled flag (and timestamp/version) is touched. Because the secret byte columns are left
        // unmodified, the SaveChanges encryption interceptor skips them — their on-disk ciphertext is untouched — and
        // a no-op toggle does not bump Version, so an enable/disable cycle no longer over-invalidates resume.
        var changed = entity.Enabled != enabled;
        entity.Enabled = enabled;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (changed)
        {
            entity.Version++;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.McpServers
                                     .FirstOrDefaultAsync(server => server.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.McpServers.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<McpServerRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.McpServers
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(server => server.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.McpServers
                                       .AsNoTracking()
                                       .OrderBy(server => server.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlyList<McpServerRecord>> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.McpServers
                                       .AsNoTracking()
                                       .Where(server => server.Enabled)
                                       .OrderBy(server => server.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static McpServerRecord ToRecord(McpServerRegistration entity)
    {
        return new McpServerRecord(entity.Id,
            entity.Name,
            entity.Description is null ? null : Decode(entity.Description),
            (McpTransportKind)entity.TransportKind,
            entity.Command,
            DecodeArguments(entity.ArgumentsJson),
            entity.WorkingDirectory,
            DecodeEnvironment(entity.EnvJson),
            entity.Url,
            entity.Enabled,
            entity.Version,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
    }

    private static byte[]? EncodeOptional(string? value)
    {
        return value is null ? null : Encoding.UTF8.GetBytes(value);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }

    private static byte[] EncodeArguments(IReadOnlyList<string> arguments)
    {
        return JsonSerializer.SerializeToUtf8Bytes(arguments, SerializerOptions);
    }

    private static byte[] EncodeEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        return JsonSerializer.SerializeToUtf8Bytes(environment, SerializerOptions);
    }

    private static IReadOnlyList<string> DecodeArguments(byte[]? json)
    {
        return json is null ? [] : JsonSerializer.Deserialize<List<string>>(json, SerializerOptions) ?? [];
    }

    private static IReadOnlyDictionary<string, string> DecodeEnvironment(byte[]? json)
    {
        return json is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static bool ArgumentsEqual(IReadOnlyList<string> current, IReadOnlyList<string> incoming)
    {
        // Argument order is connection-affecting (it is forwarded verbatim to the launched process), so the comparison
        // is order-sensitive.
        return current.SequenceEqual(incoming, StringComparer.Ordinal);
    }

    private static bool EnvironmentEqual(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> incoming)
    {
        if (current.Count != incoming.Count)
        {
            return false;
        }

        // Environment is a map, so a key reorder of the same entries is not a connection change.
        foreach (var pair in current)
        {
            if (!incoming.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
