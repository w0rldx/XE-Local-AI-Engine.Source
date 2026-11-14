namespace XE_Local_AI_Engine.Client.Services.Common;

/// <summary>A job kind whose creation <see cref="IJobCreatedPublisher" /> announces.</summary>
public enum CreatedJobKind
{
    TrainingRun,
    DatasetBuild,
    ImageJob
}
