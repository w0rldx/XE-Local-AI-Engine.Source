namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Projects the work-session service models onto the wire contracts. Entities never reach an endpoint: their text
///     columns are encrypted at rest, so a mapper reading one would hand the operator ciphertext.
/// </summary>
internal static class WorkSessionContractMapper
{
    public static WorkSessionResponse ToResponse(this WorkSessionDetail value) =>
        new()
        {
            Id = value.Id,
            Title = value.Title,
            Objective = value.Objective,
            Kind = value.Kind.ToString(),
            AgentDefinitionId = value.AgentDefinitionId,
            ConversationId = value.ConversationId,
            Status = value.Status.ToString(),
            CurrentTaskId = value.CurrentTaskId,
            StepCount = value.StepCount,
            MaxStepsPerRun = value.MaxStepsPerRun,
            LastCheckpointId = value.LastCheckpointId,
            CreatedAtUtc = value.CreatedUtc,
            UpdatedAtUtc = value.UpdatedUtc,
            Version = value.Version,
            LastSequence = value.LastSequence
        };

    public static WorkSessionSummaryResponse ToResponse(this WorkSessionSummary value) =>
        new() { Id = value.Id, Title = value.Title, Kind = value.Kind.ToString(), Status = value.Status.ToString(), AgentDefinitionId = value.AgentDefinitionId, StepCount = value.StepCount, UpdatedAtUtc = value.UpdatedUtc };

    public static WorkSessionTaskResponse ToResponse(this WorkSessionTaskDto value) =>
        new()
        {
            Id = value.Id,
            ParentTaskId = value.ParentTaskId,
            Sequence = value.Sequence,
            Title = value.Title,
            Detail = value.Detail,
            Status = value.Status.ToString(),
            BlockedReason = value.BlockedReason,
            Origin = value.Origin.ToString(),
            CreatedStep = value.CreatedStep,
            UpdatedStep = value.UpdatedStep
        };

    public static WorkSessionFindingResponse ToResponse(this WorkSessionFindingDto value) =>
        new() { Id = value.Id, TaskId = value.TaskId, Sequence = value.Sequence, Kind = value.Kind.ToString(), Text = value.Text, SourceRef = value.SourceRef, CreatedStep = value.CreatedStep, Superseded = value.Superseded };

    public static WorkSessionArtifactResponse ToResponse(this WorkSessionArtifactDto value) =>
        new()
        {
            Id = value.Id,
            Sequence = value.Sequence,
            Kind = value.Kind.ToString(),
            Name = value.Name,
            MediaType = value.MediaType,
            ContentSha256 = value.ContentSha256,
            SizeBytes = value.SizeBytes,
            IsValid = value.IsValid,
            CreatedStep = value.CreatedStep
        };

    public static WorkSessionCheckpointResponse ToResponse(this WorkSessionCheckpointDto value) =>
        new() { Id = value.Id, Sequence = value.Sequence, Step = value.Step, Summary = value.Summary, StateJson = value.StateJson, CreatedAtUtc = value.CreatedUtc };

    public static WorkSessionEventResponse ToResponse(this WorkSessionEventDto value) =>
        new() { Id = value.Id, Sequence = value.Sequence, Step = value.Step, EventType = value.EventType, DetailJson = value.DetailJson, Outcome = value.Outcome, OccurredAtUtc = value.OccurredUtc, OperationId = value.OperationId };

    public static ListWorkSessionTasksResponse ToResponse(this IReadOnlyList<WorkSessionTaskDto> value) =>
        new() { Items = [.. value.Select(ToResponse)], LastSequence = HighestSequence(value.Select(static item => item.Sequence)) };

    public static ListWorkSessionFindingsResponse ToResponse(this IReadOnlyList<WorkSessionFindingDto> value) =>
        new() { Items = [.. value.Select(ToResponse)], LastSequence = HighestSequence(value.Select(static item => item.Sequence)) };

    public static ListWorkSessionArtifactsResponse ToResponse(this IReadOnlyList<WorkSessionArtifactDto> value) =>
        new() { Items = [.. value.Select(ToResponse)], LastSequence = HighestSequence(value.Select(static item => item.Sequence)) };

    public static ListWorkSessionCheckpointsResponse ToResponse(this IReadOnlyList<WorkSessionCheckpointDto> value) =>
        new() { Items = [.. value.Select(ToResponse)], LastSequence = HighestSequence(value.Select(static item => item.Sequence)) };

    public static ListWorkSessionEventsResponse ToResponse(this IReadOnlyList<WorkSessionEventDto> value, int requestedLimit) =>
        new() { Items = [.. value.Select(ToResponse)], LastSequence = HighestSequence(value.Select(static item => item.Sequence)), HasMore = value.Count >= requestedLimit };

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
