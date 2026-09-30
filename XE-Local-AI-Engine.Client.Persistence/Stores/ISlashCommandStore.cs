namespace XE_Local_AI_Engine.Client.Persistence.Stores;

public interface ISlashCommandStore
{
    /// <summary>
    ///     Inserts a command. Throws <see cref="SlashCommandCapacityException" /> at the cap and
    ///     <see cref="SlashCommandNameConflictException" /> when the name is taken.
    /// </summary>
    Task<SlashCommandRecord> AddAsync(SlashCommandInput input, CancellationToken cancellationToken = default);

    /// <summary>Applies an edit, or returns <c>null</c> for an unknown id. A taken name throws <see cref="SlashCommandNameConflictException" />.</summary>
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

/// <summary>
///     The unique name index rejected an add or a rename: another custom command already has that name. Nothing was
///     written.
/// </summary>
public sealed class SlashCommandNameConflictException : Exception
{
    public SlashCommandNameConflictException(string message, Exception innerException) : base(message, innerException) { }
}
