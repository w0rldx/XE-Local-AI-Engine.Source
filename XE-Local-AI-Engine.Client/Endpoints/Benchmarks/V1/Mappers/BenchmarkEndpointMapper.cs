namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

internal static class BenchmarkEndpointMapper
{
    public static BenchmarkProjectDraft ToDraft(this BenchmarkProjectMutationRequest request, Guid id) =>
        new(id,
            request.Name,
            request.CoreTask,
            request.ContextTokens,
            request.AgentDefinitionId,
            request.JudgeEnabled,
            request.JudgeModelName,
            request.JudgeContextTokens,
            request.JudgePromptVersion,
            request.JudgeOutputSchemaVersion);

    public static BenchmarkProjectSummaryResponse ToSummary(this BenchmarkProjectRecord project, int runCount) =>
        new()
        {
            Id = project.Id,
            Name = project.Name,
            ContextTokens = project.ContextTokens,
            AgentDefinitionId = project.AgentDefinitionId,
            JudgeEnabled = project.JudgeEnabled,
            RunCount = runCount,
            IsFrozen = project.IsFrozen,
            Version = project.Version,
            CreatedAtUtc = project.CreatedAtUtc,
            UpdatedAtUtc = project.UpdatedAtUtc
        };

    public static BenchmarkProjectDetailResponse ToDetail(this BenchmarkProjectRecord project, int runCount) =>
        new()
        {
            Id = project.Id,
            Name = project.Name,
            CoreTask = JsonSerializer.Deserialize<string>(project.CoreTaskJson.Span)
                       ?? throw new BenchmarkValidationException("The benchmark task is required."),
            ContextTokens = project.ContextTokens,
            AgentDefinitionId = project.AgentDefinitionId,
            JudgeEnabled = project.JudgeEnabled,
            JudgeModelName = project.JudgeModelName,
            JudgeContextTokens = project.JudgeContextTokens,
            JudgePromptVersion = project.JudgePromptVersion,
            JudgeOutputSchemaVersion = project.JudgeOutputSchemaVersion,
            RunCount = runCount,
            IsFrozen = project.IsFrozen,
            Version = project.Version,
            CreatedAtUtc = project.CreatedAtUtc,
            UpdatedAtUtc = project.UpdatedAtUtc
        };

    public static BenchmarkRunSummaryResponse ToSummary(this BenchmarkRunRecord run) =>
        new()
        {
            Id = run.Id,
            ProjectId = run.ProjectId,
            PrimaryModelName = run.PrimaryModelName,
            PrimaryModelOrigin = run.PrimaryModelOrigin,
            ModelContentFingerprint = run.ModelContentFingerprint,
            AgentName = run.AgentName,
            AgentVersion = run.AgentVersion,
            RequestedContextTokens = run.RequestedContextTokens,
            PrimaryStatus = run.PrimaryStatus,
            JudgeStatus = run.JudgeStatus,
            EffectiveContextTokens = run.EffectiveContextTokens,
            DurationMs = run.DurationMs,
            TotalTokens = run.TotalTokens,
            TokensPerSecond = run.TokensPerSecond,
            UserScore = run.UserScore,
            LastStreamSequence = run.LastStreamSequence,
            Version = run.Version,
            CreatedAtUtc = run.CreatedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc
        };

    public static BenchmarkRunDetailResponse ToDetail(this BenchmarkRunRecord run) =>
        new()
        {
            Id = run.Id,
            ProjectId = run.ProjectId,
            PrimaryModelName = run.PrimaryModelName,
            PrimaryModelOrigin = run.PrimaryModelOrigin,
            ModelContentFingerprint = run.ModelContentFingerprint,
            AgentName = run.AgentName,
            AgentVersion = run.AgentVersion,
            RequestedContextTokens = run.RequestedContextTokens,
            PrimaryStatus = run.PrimaryStatus,
            JudgeStatus = run.JudgeStatus,
            EffectiveContextTokens = run.EffectiveContextTokens,
            DurationMs = run.DurationMs,
            TotalTokens = run.TotalTokens,
            TokensPerSecond = run.TokensPerSecond,
            OutputParts = ParseJson(run.OutputPartsJson),
            JudgeResult = ParseJudgeResult(run.JudgeResultJson),
            UserScore = run.UserScore,
            LastStreamSequence = run.LastStreamSequence,
            PrimaryErrorMessage = run.PrimaryErrorMessage,
            JudgeErrorMessage = run.JudgeErrorMessage,
            Version = run.Version,
            CreatedAtUtc = run.CreatedAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            PrimaryCompletedAtUtc = run.PrimaryCompletedAtUtc,
            JudgeStartedAtUtc = run.JudgeStartedAtUtc,
            JudgeCompletedAtUtc = run.JudgeCompletedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc
        };

    public static EligibleBenchmarkAgentResponse ToResponse(this BenchmarkEligibleAgent agent) =>
        new()
        {
            Id = agent.Id,
            Name = agent.Name,
            Version = agent.Version
        };

    public static EligibleBenchmarkModelResponse ToResponse(this BenchmarkEligibleModel model) =>
        new()
        {
            ModelName = model.ModelName,
            MaxContextTokens = model.MaxContextTokens,
            EffectiveContextTokens = model.EffectiveContextTokens,
            Origin = model.Origin,
            ModelContentFingerprint = model.ModelContentFingerprint,
            SupportsTools = model.SupportsTools
        };

    private static JsonElement? ParseJson(ReadOnlyMemory<byte>? payload)
    {
        if (payload is not { } value || value.IsEmpty)
        {
            return null;
        }

        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static BenchmarkJudgeResultV1? ParseJudgeResult(ReadOnlyMemory<byte>? payload)
    {
        if (payload is not { } value || value.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BenchmarkJudgeResultV1>(value.Span);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
