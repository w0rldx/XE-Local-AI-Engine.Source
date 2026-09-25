namespace XE_Local_AI_Engine.Client.Services.Transcription.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The default <see cref="ITranscriptionEventPublisher" />: drops every event.
/// </summary>
/// <remarks>
///     Registered with <c>TryAddSingleton</c> so the application layer resolves without a host, and superseded by the
///     SignalR-backed publisher the host registers after it. The same floor the image job publisher uses.
/// </remarks>
public sealed class NullTranscriptionEventPublisher : ITranscriptionEventPublisher
{
    public Task PublishSegmentAsync(Guid sessionId,
        long seq,
        TranscriptChannel channel,
        long startMs,
        long endMs,
        string text,
        double? confidence,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task PublishPartialAsync(Guid sessionId, TranscriptChannel channel, string text, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task PublishStatusAsync(Guid sessionId, LiveEndReason reason, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task PublishCatchUpAsync(Guid sessionId, long bufferedMs, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task PublishAdmissionClosedAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
