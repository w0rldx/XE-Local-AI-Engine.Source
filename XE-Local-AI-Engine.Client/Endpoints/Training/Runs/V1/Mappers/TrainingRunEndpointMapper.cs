namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1.Mappers;

using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

internal static class TrainingRunEndpointMapper
{
    public static TrainingRunResponse ToResponse(this TrainingRunRecord record)
    {
        var progress = TrainingEndpointSupport.Read<TrainingRunProgressV1>(record.ProgressJson);
        return new TrainingRunResponse
        {
            Id = record.Id,
            DatasetId = record.DatasetId,
            BaseArtifactId = record.BaseArtifactId,
            Status = record.Status.ToString(),
            DatasetRevision = record.DatasetRevision,
            DatasetContentFingerprint = record.DatasetContentFingerprint,
            WorkStatus = record.WorkStatus?.ToString(),
            ErrorMessage = record.ErrorMessage,
            LogTail = record.LogTail,
            Progress = progress is null
                ? null
                : new TrainingRunProgressResponse
                {
                    Phase = progress.Phase,
                    Step = progress.Step,
                    TotalSteps = progress.TotalSteps,
                    Epoch = progress.Epoch,
                    Loss = progress.Loss,
                    LearningRate = progress.LearningRate,
                    VramBytes = progress.VramBytes
                },
            // The freeze and the license confirmation stay server-side: neither is something the UI renders, and both
            // carry more of the dataset's shape than a run row needs to publish.
            Options = TrainingEndpointSupport.Read<TrainingRunOptionsV1>(record.OptionsJson)?.ToPayload(),
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }

    public static TrainingRunOptionsPayload ToPayload(this TrainingRunOptionsV1 options) =>
        new()
        {
            MaxSeqLength = options.MaxSeqLength,
            LoraR = options.LoraR,
            LoraAlpha = options.LoraAlpha,
            LoraDropout = options.LoraDropout,
            PerDeviceTrainBatchSize = options.PerDeviceTrainBatchSize,
            GradientAccumulationSteps = options.GradientAccumulationSteps,
            LearningRate = options.LearningRate,
            WarmupRatio = options.WarmupRatio,
            Epochs = options.Epochs,
            Seed = options.Seed,
            Optimizer = options.Optimizer
        };

    public static TrainingRunOptionsV1 ToDomain(this TrainingRunOptionsPayload payload) =>
        new()
        {
            MaxSeqLength = payload.MaxSeqLength,
            LoraR = payload.LoraR,
            LoraAlpha = payload.LoraAlpha,
            LoraDropout = payload.LoraDropout,
            PerDeviceTrainBatchSize = payload.PerDeviceTrainBatchSize,
            GradientAccumulationSteps = payload.GradientAccumulationSteps,
            LearningRate = payload.LearningRate,
            WarmupRatio = payload.WarmupRatio,
            Epochs = payload.Epochs,
            Seed = payload.Seed,
            Optimizer = payload.Optimizer
        };

    public static TrainingRunDefaultsResponse ToResponse(this TrainingRunDefaults defaults,
        TrainingLicenseGateView? license,
        IReadOnlyList<InstalledBaseModelLink> linkedModelSuggestions) =>
        new()
        {
            LinkedModelSuggestions = linkedModelSuggestions
                                     .Select(link => new TrainingRunLinkedModelResponse
                                     {
                                         ModelName = link.ModelName,
                                         RepoId = link.RepoId,
                                         ContentFingerprint = link.ContentFingerprint
                                     })
                                     .ToArray(),
            Options = defaults.Options.ToPayload(),
            Estimate = new TrainingRunFootprintResponse
            {
                GpuBytes = defaults.Estimate.GpuBytes,
                RamBytes = defaults.Estimate.RamBytes,
                ParameterCount = defaults.Estimate.ParameterCount,
                TrainableParameterCount = defaults.Estimate.TrainableParameterCount,
                Experimental = defaults.Estimate.Experimental
            },
            AvailableVramBytes = defaults.AvailableVramBytes,
            VramKnown = defaults.VramKnown,
            Fits = defaults.Fits,
            RejectionReason = defaults.RejectionReason,
            License = license is null
                ? null
                : new TrainingRunLicenseResponse
                {
                    RepoId = license.RepoId,
                    License = license.License,
                    IsGated = license.IsGated,
                    MetadataPresent = license.MetadataPresent,
                    ConfirmationText = license.ConfirmationText
                }
        };
}
