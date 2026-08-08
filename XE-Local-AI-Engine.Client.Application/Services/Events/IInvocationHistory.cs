namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Abstraction for invocation history behavior.
/// </summary>
public interface IInvocationHistory
{
    int Capacity { get; }
    event EventHandler<InvocationHistoryEntryAddedEventArgs>? EntryAdded;

    IReadOnlyList<InvocationHistoryEntry> Snapshot();

    void Record(InvocationState state);
}

/// <summary>
///     Event payload for invocation history entry added notifications.
/// </summary>
public sealed class InvocationHistoryEntryAddedEventArgs(InvocationHistoryEntry entry) : EventArgs
{
    public InvocationHistoryEntry Entry { get; } = entry ?? throw new ArgumentNullException(nameof(entry));
}
