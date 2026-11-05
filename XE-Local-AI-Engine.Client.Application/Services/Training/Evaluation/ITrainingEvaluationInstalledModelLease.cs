namespace XE_Local_AI_Engine.Client.Services.Training.Evaluation;

public interface ITrainingEvaluationInstalledModelLease : IAsyncDisposable
{
    string ModelFilePath { get; }
    string ModelContentFingerprint { get; }
    string ModelSha256 { get; }
    long ModelSizeBytes { get; }
}
