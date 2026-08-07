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

public sealed record SlashCommandCatalogItem(Guid? Id, string Name, string? Description, string Source, SlashCommandActionType ActionType, string Prompt);

public sealed class SlashCommandValidationException(string message) : Exception(message);

public sealed class SlashCommandConflictException(string message, Exception? innerException = null) : Exception(message, innerException);
