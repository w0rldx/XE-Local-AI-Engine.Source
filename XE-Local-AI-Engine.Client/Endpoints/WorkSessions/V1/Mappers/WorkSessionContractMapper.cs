namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Projects the work-session service models onto the wire contracts. Entities never reach an endpoint: their text
///     columns are encrypted at rest, so a mapper reading one would hand the operator ciphertext.
/// </summary>
internal static class WorkSessionContractMapper
{
    public static WorkSessionResponse ToResponse(this WorkSessionDetail value) =>
        new(value.Id,
            value.Title,
            value.Objective,
            value.Kind.ToString(),
            value.AgentDefinitionId,
            value.ConversationId,
            value.Status.ToString(),
            value.CurrentTaskId,
            value.StepCount,
            value.MaxStepsPerRun,
            value.LastCheckpointId,
            value.CreatedUtc,
            value.UpdatedUtc,
            value.Version,
            value.LastSequence);

    public static WorkSessionSummaryResponse ToResponse(this WorkSessionSummary value) =>
        new(value.Id, value.Title, value.Kind.ToString(), value.Status.ToString(), value.AgentDefinitionId, value.StepCount, value.UpdatedUtc);

    public static WorkSessionTaskResponse ToResponse(this WorkSessionTaskDto value) =>
        new(value.Id,
            value.ParentTaskId,
            value.Sequence,
            value.Title,
            value.Detail,
            value.Status.ToString(),
            value.BlockedReason,
            value.Origin.ToString(),
            value.CreatedStep,
            value.UpdatedStep);

    public static WorkSessionFindingResponse ToResponse(this WorkSessionFindingDto value) =>
        new(value.Id, value.TaskId, value.Sequence, value.Kind.ToString(), value.Text, value.SourceRef, value.CreatedStep, value.Superseded);

    public static WorkSessionArtifactResponse ToResponse(this WorkSessionArtifactDto value) =>
        new(value.Id,
            value.Sequence,
            value.Kind.ToString(),
            value.Name,
            value.MediaType,
            value.ContentSha256,
            value.SizeBytes,
            value.IsValid,
            value.CreatedStep);

    public static WorkSessionCheckpointResponse ToResponse(this WorkSessionCheckpointDto value) =>
        new(value.Id, value.Sequence, value.Step, value.Summary, value.StateJson, value.CreatedUtc);

    public static WorkSessionEventResponse ToResponse(this WorkSessionEventDto value) =>
        new(value.Id, value.Sequence, value.Step, value.EventType, value.DetailJson, value.Outcome, value.OccurredUtc, value.OperationId);

    public static ListWorkSessionTasksResponse ToResponse(this IReadOnlyList<WorkSessionTaskDto> value) =>
        new([.. value.Select(ToResponse)], HighestSequence(value.Select(static item => item.Sequence)));

    public static ListWorkSessionFindingsResponse ToResponse(this IReadOnlyList<WorkSessionFindingDto> value) =>
        new([.. value.Select(ToResponse)], HighestSequence(value.Select(static item => item.Sequence)));

    public static ListWorkSessionArtifactsResponse ToResponse(this IReadOnlyList<WorkSessionArtifactDto> value) =>
        new([.. value.Select(ToResponse)], HighestSequence(value.Select(static item => item.Sequence)));

    public static ListWorkSessionCheckpointsResponse ToResponse(this IReadOnlyList<WorkSessionCheckpointDto> value) =>
        new([.. value.Select(ToResponse)], HighestSequence(value.Select(static item => item.Sequence)));

    public static ListWorkSessionEventsResponse ToResponse(this IReadOnlyList<WorkSessionEventDto> value, int requestedLimit) =>
        new([.. value.Select(ToResponse)], HighestSequence(value.Select(static item => item.Sequence)), value.Count >= requestedLimit);

    /// <summary>
    ///     The page's HIGHEST sequence, not its last row's. The feeds are ordered by creation step so a re-stamped task
    ///     keeps its place in the plan, which means the newest sequence can sit anywhere in the page; paging from the
    ///     last row would replay every row after it, forever.
    /// </summary>
    private static long HighestSequence(IEnumerable<long> sequences)
    {
        var highest = 0L;
        foreach (var sequence in sequences)
        {
            highest = Math.Max(highest, sequence);
        }

        return highest;
    }
}
