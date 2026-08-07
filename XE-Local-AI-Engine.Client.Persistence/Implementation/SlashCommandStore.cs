namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class SlashCommandStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : ISlashCommandStore
{
    private const int MaximumCustomCommands = 100;
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<SlashCommandRecord> AddAsync(SlashCommandInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await _dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = (SqliteConnection)_dbContext.Database.GetDbConnection();
        await using var transaction = BeginImmediateTransaction(connection);
        _ = await _dbContext.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _dbContext.SlashCommands.CountAsync(cancellationToken).ConfigureAwait(false) >= MaximumCustomCommands)
            {
                throw new SlashCommandCapacityException();
            }

            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var entity = new SlashCommand
            {
                Id = Guid.NewGuid(), Name = input.Name, Description = EncodeOptional(input.Description),
                ActionType = (int)input.ActionType, ActionConfiguration = SerializeAction(input.Prompt),
                CreatedAtUtc = now, UpdatedAtUtc = now
            };
            _ = _dbContext.SlashCommands.Add(entity);
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(entity);
        }
        finally
        {
            _ = await _dbContext.Database.UseTransactionAsync(null, CancellationToken.None).ConfigureAwait(false);
            await _dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    public async Task<SlashCommandRecord?> UpdateAsync(Guid id, SlashCommandInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var entity = await _dbContext.SlashCommands.FirstOrDefaultAsync(command => command.Id == id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }
        entity.Name = input.Name;
        entity.Description = EncodeOptional(input.Description);
        entity.ActionType = (int)input.ActionType;
        entity.ActionConfiguration = SerializeAction(input.Prompt);
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.SlashCommands.FirstOrDefaultAsync(command => command.Id == id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }
        _ = _dbContext.SlashCommands.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<SlashCommandRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.SlashCommands.AsNoTracking().FirstOrDefaultAsync(command => command.Id == id, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<SlashCommandRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.SlashCommands.AsNoTracking().OrderBy(command => command.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToRecord).ToArray();
    }

    private static byte[]? EncodeOptional(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);

    private static byte[] SerializeAction(string prompt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject(); writer.WriteString("type", "sendPrompt"); writer.WriteNumber("version", 1); writer.WriteString("prompt", prompt); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static SlashCommandRecord ToRecord(SlashCommand entity)
    {
        ValidatePersistedName(entity.Name);
        if (entity.ActionType != (int)SlashCommandActionType.SendPrompt)
        {
            throw new InvalidDataException("Unsupported slash command action type.");
        }

        string prompt;
        try
        {
            prompt = DeserializeAction(entity.ActionConfiguration);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid slash command action configuration.", exception);
        }

        if (string.IsNullOrWhiteSpace(prompt) || Encoding.UTF8.GetByteCount(prompt) > 20_000)
        {
            throw new InvalidDataException("Invalid slash command prompt.");
        }
        var description = entity.Description is null ? null : Encoding.UTF8.GetString(entity.Description);
        if (description is not null && (description.Length == 0 || !string.Equals(description, description.Trim(), StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(description) > 1_024))
        {
            throw new InvalidDataException("Invalid slash command description.");
        }
        return new SlashCommandRecord(entity.Id, entity.Name, description, SlashCommandActionType.SendPrompt, prompt, entity.CreatedAtUtc, entity.UpdatedAtUtc);
    }

    private static void ValidatePersistedName(string name)
    {
        if (name.Length is < 1 or > 64 || string.Equals(name, "ping", StringComparison.OrdinalIgnoreCase)
            || name[0] == '-' || name[^1] == '-' || name.Contains("--", StringComparison.Ordinal)
            || name.Any(static character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-'))
        {
            throw new InvalidDataException("Invalid persisted slash command name.");
        }
    }

    private static string DeserializeAction(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("Invalid slash command action configuration.");
        }

        string? type = null;
        string? prompt = null;
        int? version = null;
        var properties = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new InvalidDataException("Invalid slash command action configuration.");
            }

            var property = reader.GetString() ?? throw new InvalidDataException("Invalid slash command action configuration.");
            if (!properties.Add(property) || !reader.Read())
            {
                throw new InvalidDataException("Invalid slash command action configuration.");
            }

            switch (property)
            {
                case "type" when reader.TokenType == JsonTokenType.String:
                    type = reader.GetString();
                    break;
                case "version" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value):
                    version = value;
                    break;
                case "prompt" when reader.TokenType == JsonTokenType.String:
                    prompt = reader.GetString();
                    break;
                default:
                    throw new InvalidDataException("Invalid slash command action configuration.");
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || properties.Count != 3
            || !string.Equals(type, "sendPrompt", StringComparison.Ordinal) || version != 1 || prompt is null)
        {
            throw new InvalidDataException("Invalid slash command action configuration.");
        }

        return prompt;
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method",
        Justification = "Microsoft.Data.Sqlite has no async transaction overload that preserves BEGIN IMMEDIATE serialization.")]
    private static SqliteTransaction BeginImmediateTransaction(SqliteConnection connection) => connection.BeginTransaction(deferred: false);
}
