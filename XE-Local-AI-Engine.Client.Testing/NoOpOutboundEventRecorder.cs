namespace XE_Local_AI_Engine.Client.Testing;

public sealed class NoOpOutboundEventRecorder : IOutboundEventRecorder
{
    public Task RecordAsync(string method, object? payload, long sequenceNumber, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
