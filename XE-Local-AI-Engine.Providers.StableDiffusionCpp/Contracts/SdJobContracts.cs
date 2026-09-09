namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     The status of an sd-server async job, mapped from the <c>GET /sdcpp/v1/jobs/{id}</c> response.
/// </summary>
internal enum SdJobStatus
{
    /// <summary>Accepted, waiting for a generation slot.</summary>
    Queued,

    /// <summary>Actively generating.</summary>
    Generating,

    /// <summary>Finished; the decoded image is available on the state.</summary>
    Completed,

    /// <summary>Failed; a sanitized error message is available on the state.</summary>
    Failed,

    /// <summary>Cancelled by a prior cancel request.</summary>
    Cancelled,

    /// <summary><c>410 Gone</c> — the job's 600s result TTL elapsed and the record was purged.</summary>
    Expired,

    /// <summary><c>404</c> — the server does not know this job id (should not happen for a job we submitted).</summary>
    Unknown
}

/// <summary>The outcome of a <c>POST /sdcpp/v1/jobs/{id}/cancel</c> call.</summary>
internal enum SdCancelOutcome
{
    /// <summary><c>200</c> — the job was queued (now cancelled) or already terminal (idempotent).</summary>
    Cancelled,

    /// <summary><c>409 Conflict</c> — the job is generating and cannot be interrupted over HTTP; abort by tree-kill + restart.</summary>
    Generating,

    /// <summary><c>404</c>/<c>410</c> — the server no longer tracks this job; nothing to cancel.</summary>
    NotFoundOrGone
}

/// <summary>A resolved job-poll observation: status plus any completed-image / error payload.</summary>
internal sealed record SdJobState
{
    public required SdJobStatus Status { get; init; }

    /// <summary>Queue position while <see cref="SdJobStatus.Queued" />, when the server reports one.</summary>
    public int? QueuePosition { get; init; }

    /// <summary>The decoded PNG bytes when <see cref="SdJobStatus.Completed" />; otherwise <see langword="null" />.</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>The seed the server actually used, when it reported one.</summary>
    public long? Seed { get; init; }
}
