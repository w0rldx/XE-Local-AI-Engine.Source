namespace XE_Local_AI_Engine.Client.Testing;

/// <summary>
///     Represents no op outbound event recorder.
/// </summary>
public sealed class NoOpOutboundEventRecorder : IOutboundEventRecorder
{
    public Task RecordAsync(string method, object? payload, long sequenceNumber, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
