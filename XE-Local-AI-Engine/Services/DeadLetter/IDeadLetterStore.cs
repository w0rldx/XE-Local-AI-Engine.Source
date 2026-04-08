namespace XE_Local_AI_Engine.Services.DeadLetter
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using XE_Local_AI_Engine.Models;

    public interface IDeadLetterStore
    {
        Task EnqueueAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<InvocationFailedPayload>> GetPendingAsync(CancellationToken cancellationToken = default);

        Task RemoveAsync(Guid invocationId, CancellationToken cancellationToken = default);

        long GetCurrentSizeBytes();
    }
}
