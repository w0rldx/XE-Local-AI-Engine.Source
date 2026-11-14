namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>
///     Client-method name broadcast on the training-run, dataset-generation and image-job hubs when a job of that hub's
///     kind is created, so the operations feed re-reads that list.
/// </summary>
public static class JobCreatedHubEvents
{
    public const string Created = "job.created";
}

/// <summary>Sanitized creation payload: the job id and its kind name, nothing else.</summary>
public sealed class JobCreatedHubEvent
{
    public required Guid Id { get; init; }

    public required string Kind { get; init; }
}
