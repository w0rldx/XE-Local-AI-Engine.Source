namespace XE_Local_AI_Engine.Client.Services.Training.Export;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public sealed class ArtifactPromotionCompensationException(
    GgufImportCommitReceipt commitReceipt,
    Exception persistenceFailure,
    Exception rollbackFailure)
    : AggregateException("The promoted registry entry could not be recorded or rolled back; recovery receipt evidence is attached.",
        persistenceFailure,
        rollbackFailure)
{
    public GgufImportCommitReceipt CommitReceipt { get; } = commitReceipt;
}
