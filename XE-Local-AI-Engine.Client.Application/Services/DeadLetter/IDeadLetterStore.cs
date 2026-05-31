namespace XE_Local_AI_Engine.Client.Services.DeadLetter;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Persistence boundary for i dead letter data.
/// </summary>
public interface IDeadLetterStore
{
    Task EnqueueAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InvocationFailedPayload>> GetPendingAsync(CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid invocationId, CancellationToken cancellationToken = default);

    long GetCurrentSizeBytes();
}
