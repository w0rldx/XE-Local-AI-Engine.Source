namespace XE_Local_AI_Engine.Client.Services.Training.Export;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public sealed class ArtifactPromotionCompensationException : AggregateException
{
    public ArtifactPromotionCompensationException(GgufImportCommitReceipt commitReceipt,
        Exception persistenceFailure,
        Exception rollbackFailure) : base("The promoted registry entry could not be recorded or rolled back; recovery receipt evidence is attached.",
        persistenceFailure,
        rollbackFailure)
    {
        CommitReceipt = commitReceipt;
    }

    public GgufImportCommitReceipt CommitReceipt { get; }
}
