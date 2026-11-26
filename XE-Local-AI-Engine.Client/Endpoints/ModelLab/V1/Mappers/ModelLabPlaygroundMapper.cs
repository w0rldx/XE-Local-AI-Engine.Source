namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.ModelLab;

internal static class ModelLabPlaygroundMapper
{
    public static ModelLabPlaygroundConfigV1 ToConfig(StartModelLabPlaygroundRunRequest request) =>
        new()
        {
            Dataset = request.Dataset,
            LearningRate = request.LearningRate,
            Residual = request.Residual,
            SharedBlock = request.SharedBlock,
            Depth = request.Depth,
            Hidden = request.Hidden,
            Steps = request.Steps,
            Seed = request.Seed
        };

    public static ModelLabPlaygroundRunResponse ToResponse(ModelLabPlaygroundRunSummary run)
    {
        var config = run.Result.Config;
        return new ModelLabPlaygroundRunResponse
        {
            Id = run.Id,
            Template = run.TemplateId,
            Config = new ModelLabPlaygroundConfigResponse
            {
                Dataset = config.Dataset,
                LearningRate = config.LearningRate,
                Residual = config.Residual,
                SharedBlock = config.SharedBlock,
                Depth = config.Depth,
                Hidden = config.Hidden,
                Steps = config.Steps,
                Seed = config.Seed
            },
            Outcome = new ModelLabPlaygroundOutcomeResponse
            {
                Kind = run.Result.Outcome.Kind,
                AtStep = run.Result.Outcome.AtStep,
                UpdatesApplied = run.Result.Outcome.UpdatesApplied
            },
            FinalLoss = run.Result.FinalLoss,
            FinalAccuracy = run.Result.FinalAccuracy,
            TraceByteCount = run.TraceByteCount,
            CreatedAtUtc = run.CreatedAtUtc
        };
    }

    public static ModelLabPlaygroundBlockedResponse Blocked(ModelLabPlaygroundRefusal reason, string message) =>
        new()
        {
            Reason = reason,
            Message = message
        };
}
