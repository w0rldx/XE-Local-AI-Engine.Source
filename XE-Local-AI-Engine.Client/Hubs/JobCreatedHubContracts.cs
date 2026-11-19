namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>
///     Client-method names broadcast on a job kind's own hub (training runs, Model Lab experiments and base checkpoint
///     downloads on the training-run hub, dataset builds and image jobs on theirs), so the operations feed re-reads that list.
/// </summary>
public static class JobCreatedHubEvents
{
    public const string Created = "job.created";

    /// <summary>Only for a kind whose end reaches no group: a base checkpoint download.</summary>
    public const string Ended = "job.ended";
}

/// <summary>Sanitized payload: the job id and its kind name, nothing else.</summary>
public sealed class JobCreatedHubEvent
{
    public required Guid Id { get; init; }

    public required string Kind { get; init; }
}
