namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public sealed record CommittedModelDeletion(
    Guid OperationId,
    string RequestedModelName,
    IReadOnlyList<string> RemovedModelNames,
    GgufDeletionStageReceipt StageReceipt);

public interface ILocalModelDeletionCoordinator
{
    Task<CommittedModelDeletion> CommitDeleteAsync(string modelName, CancellationToken cancellationToken = default);
    Task PurgeAfterSuccessAsync(CommittedModelDeletion committedDeletion, CancellationToken cancellationToken = default);
}

/// <summary>
///     Thrown when a base model cannot be deleted because installed LoRA adapters launch against it. An adapter
///     carries no weights of its own, so removing the base leaves every dependent adapter permanently unlaunchable.
///     The global <c>ConflictExceptionHandler</c> turns it into a 409 with
///     <c>conflictType = InstalledModelHasDependentAdapters</c> — endpoints must let it propagate, never catch it.
/// </summary>
public sealed class InstalledModelDependentAdaptersException()
    : InvalidOperationException("Installed LoRA adapters apply to this model. Remove them before deleting it.");

/// <summary>
///     Thrown when an alias of the model being deleted is mapped to a runtime provider other than llama.cpp, so the
///     GGUF deletion path is not the owner of that alias. Mapped to a 409 with
///     <c>conflictType = InstalledModelProviderConflict</c>.
/// </summary>
public sealed class InstalledModelProviderConflictException()
    : InvalidOperationException("The model is mapped to a different runtime provider. Refresh the model list and try again.");

/// <summary>
///     Thrown when a concurrent model mutation moved the provider map on past the revision this deletion (or its
///     compensating rollback) read, so the write would clobber someone else's change. Mapped to a 409 with
///     <c>conflictType = InstalledModelProviderMapSuperseded</c>; the operation is retryable after a refresh.
/// </summary>
public sealed class InstalledModelProviderMapSupersededException()
    : InvalidOperationException("Another model change completed while this delete was running. Refresh the model list and try again.");
