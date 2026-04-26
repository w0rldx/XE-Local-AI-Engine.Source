namespace XE_Local_AI_Engine.Client.Testing;

public interface IOutboundEventRecorder
{
    Task RecordAsync(string method, object? payload, long sequenceNumber, CancellationToken ct);
}
