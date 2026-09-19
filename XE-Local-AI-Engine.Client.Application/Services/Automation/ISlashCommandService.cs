namespace XE_Local_AI_Engine.Client.Services.Automation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface ISlashCommandService
{
    Task<IReadOnlyList<SlashCommandCatalogItem>> ListAsync(CancellationToken cancellationToken = default);
    Task<SlashCommandCatalogItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SlashCommandCatalogItem> CreateAsync(SlashCommandInput input, CancellationToken cancellationToken = default);
    Task<SlashCommandCatalogItem?> UpdateAsync(Guid id, SlashCommandInput input, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
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

public sealed class SlashCommandConflictException : Exception
{
    public SlashCommandConflictException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
