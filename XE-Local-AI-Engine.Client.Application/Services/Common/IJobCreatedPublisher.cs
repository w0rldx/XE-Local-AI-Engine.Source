namespace XE_Local_AI_Engine.Client.Services.Common;

/// <summary>
///     Announces a newly created job to every connected client, so an operations feed that holds no running work still
///     learns of a job another tab or an API caller started.
/// </summary>
/// <remarks>
///     A job's own events reach only the clients that joined its group, so this is the one broadcast. A base checkpoint
///     download has no group at all, so its end is announced the same way. The payload is the id and kind only.
///     Best-effort: an implementation never throws. The default is a no-op; the Client host supplies the hub-backed one.
/// </remarks>
public interface IJobCreatedPublisher
{
    /// <summary>Announces that the job of <paramref name="kind" /> with <paramref name="jobId" /> was created.</summary>
    Task PublishCreatedAsync(CreatedJobKind kind, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Announces that the job of <paramref name="kind" /> with <paramref name="jobId" /> ended, whatever its outcome.</summary>
    Task PublishEndedAsync(CreatedJobKind kind, Guid jobId, CancellationToken cancellationToken = default);
}
