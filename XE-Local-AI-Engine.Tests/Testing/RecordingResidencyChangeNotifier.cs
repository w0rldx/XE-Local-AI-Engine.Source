namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>Counts residency ticks, so a supervisor or gate test can assert a transition raised one.</summary>
internal sealed class RecordingResidencyChangeNotifier : IRuntimeResidencyChangeNotifier
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void NotifyChanged() =>
        Interlocked.Increment(ref _count);
}
