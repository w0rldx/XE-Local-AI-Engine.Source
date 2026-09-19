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

public sealed class SlashCommandInput
{
    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required SlashCommandActionType ActionType { get; init; }

    public required string Prompt { get; init; }
}

public sealed class SlashCommandRecord
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required SlashCommandActionType ActionType { get; init; }

    public required string Prompt { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class SlashCommandCapacityException : Exception
{
    public SlashCommandCapacityException() : base("At most 100 custom commands can be configured.") { }
}
