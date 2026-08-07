namespace XE_Local_AI_Engine.Client.Persistence.Stores;

public interface ISlashCommandStore
{
    Task<SlashCommandRecord> AddAsync(SlashCommandInput input, CancellationToken cancellationToken = default);
    Task<SlashCommandRecord?> UpdateAsync(Guid id, SlashCommandInput input, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SlashCommandRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SlashCommandRecord>> ListAsync(CancellationToken cancellationToken = default);
}

public enum SlashCommandActionType
{
    Unknown = 0,
    SendPrompt = 1
}

public sealed record SlashCommandInput(string Name, string? Description, SlashCommandActionType ActionType, string Prompt);

public sealed record SlashCommandRecord(Guid Id, string Name, string? Description, SlashCommandActionType ActionType, string Prompt, long CreatedAtUtc, long UpdatedAtUtc);

public sealed class SlashCommandCapacityException : Exception
{
    public SlashCommandCapacityException() : base("At most 100 custom commands can be configured.") { }
}
