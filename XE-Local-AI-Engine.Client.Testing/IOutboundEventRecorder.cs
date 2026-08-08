namespace XE_Local_AI_Engine.Client.Testing;

/// <summary>
///     Abstraction for outbound event recorder behavior.
/// </summary>
public interface IOutboundEventRecorder
{
    Task RecordAsync(string method, object? payload, long sequenceNumber, CancellationToken ct);
}
