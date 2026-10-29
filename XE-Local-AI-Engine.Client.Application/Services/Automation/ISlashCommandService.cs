namespace XE_Local_AI_Engine.Client.Services.Automation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface ISlashCommandService
{
    Task<IReadOnlyList<SlashCommandCatalogItem>> ListAsync(CancellationToken cancellationToken = default);
    /// <exception cref="SlashCommandNotFoundException">No custom command has <paramref name="id" />.</exception>
    Task<SlashCommandCatalogItem> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SlashCommandCatalogItem> CreateAsync(SlashCommandInput input, CancellationToken cancellationToken = default);
    /// <exception cref="SlashCommandNotFoundException">No custom command has <paramref name="id" />.</exception>
    Task<SlashCommandCatalogItem> UpdateAsync(Guid id, SlashCommandInput input, CancellationToken cancellationToken = default);
    /// <exception cref="SlashCommandNotFoundException">No custom command has <paramref name="id" />.</exception>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class SlashCommandCatalogItem
{
    public required Guid? Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string Source { get; init; }

    public required SlashCommandActionType ActionType { get; init; }

    public required string Prompt { get; init; }
}

public sealed class SlashCommandValidationException : Exception
{
    public SlashCommandValidationException(string message) : base(message)
    {
    }
}

/// <summary>No custom slash command has the id the client supplied.</summary>
public sealed class SlashCommandNotFoundException : ResourceNotFoundException
{
    public SlashCommandNotFoundException(Guid id) : base($"No slash command has id '{id}'.")
    {
    }
}

public sealed class SlashCommandConflictException : Exception
{
    public SlashCommandConflictException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
