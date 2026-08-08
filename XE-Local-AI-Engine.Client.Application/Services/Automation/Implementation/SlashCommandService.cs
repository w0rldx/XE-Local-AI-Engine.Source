namespace XE_Local_AI_Engine.Client.Services.Automation.Implementation;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class SlashCommandService(ISlashCommandStore store) : ISlashCommandService
{
    private static readonly SlashCommandCatalogItem Ping = new(null, "ping", "Test the current chat agent.", "builtIn", SlashCommandActionType.SendPrompt,
        "Respond with exactly PONG and nothing else.");

    private readonly ISlashCommandStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<IReadOnlyList<SlashCommandCatalogItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var custom = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return custom.Select(ToCatalogItem).Append(Ping).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<SlashCommandCatalogItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToCatalogItem(record);
    }

    public async Task<SlashCommandCatalogItem> CreateAsync(SlashCommandInput input, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(input);
        try { return ToCatalogItem(await _store.AddAsync(normalized, cancellationToken).ConfigureAwait(false)); }
        catch (SlashCommandCapacityException exception) { throw new SlashCommandConflictException(exception.Message, exception); }
        catch (DbUpdateException exception) when (IsUniqueNameViolation(exception))
        {
            throw new SlashCommandConflictException($"A command named '/{normalized.Name}' already exists.", exception);
        }
    }

    public async Task<SlashCommandCatalogItem?> UpdateAsync(Guid id, SlashCommandInput input, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(input);
        try
        {
            var record = await _store.UpdateAsync(id, normalized, cancellationToken).ConfigureAwait(false);
            return record is null ? null : ToCatalogItem(record);
        }
        catch (DbUpdateException exception) when (IsUniqueNameViolation(exception))
        {
            throw new SlashCommandConflictException($"A command named '/{normalized.Name}' already exists.", exception);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(id, cancellationToken);

    private static SlashCommandInput Normalize(SlashCommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = input.Name.Trim();
        var prompt = input.Prompt.Trim();
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (name.Length is < 1 or > 64 || !NamePattern().IsMatch(name))
        {
            throw new SlashCommandValidationException("Name must contain 1-64 lowercase letters, digits, or single hyphens between segments.");
        }

        if (string.Equals(name, Ping.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new SlashCommandValidationException("The command name 'ping' is reserved.");
        }

        if (input.ActionType != SlashCommandActionType.SendPrompt)
        {
            throw new SlashCommandValidationException("Only the sendPrompt action is supported.");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new SlashCommandValidationException("Prompt is required.");
        }

        if (Encoding.UTF8.GetByteCount(prompt) > 20_000)
        {
            throw new SlashCommandValidationException("Prompt must be at most 20,000 UTF-8 bytes.");
        }

        if (description is not null && Encoding.UTF8.GetByteCount(description) > 1_024)
        {
            throw new SlashCommandValidationException("Description must be at most 1,024 UTF-8 bytes.");
        }

        return new SlashCommandInput(name, description, input.ActionType, prompt);
    }

    private static SlashCommandCatalogItem ToCatalogItem(SlashCommandRecord record) =>
        new(record.Id, record.Name, record.Description, "custom", record.ActionType, record.Prompt);

    private static bool IsUniqueNameViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NamePattern();
}
