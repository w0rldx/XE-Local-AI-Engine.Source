namespace XE_Local_AI_Engine.Client.Services.Events;

public interface IInvocationHistory
{
    int Capacity { get; }
    event EventHandler<InvocationHistoryEntryAddedEventArgs>? EntryAdded;

    IReadOnlyList<InvocationHistoryEntry> Snapshot();

    void Record(InvocationState state);
}

public sealed class InvocationHistoryEntryAddedEventArgs(InvocationHistoryEntry entry) : EventArgs
{
    public InvocationHistoryEntry Entry { get; } = entry ?? throw new ArgumentNullException(nameof(entry));
}
