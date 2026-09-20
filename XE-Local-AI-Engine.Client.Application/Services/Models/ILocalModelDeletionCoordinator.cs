namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public sealed record CommittedModelDeletion
{
    public required Guid OperationId { get; init; }

    public required string RequestedModelName { get; init; }

    public required IReadOnlyList<string> RemovedModelNames { get; init; }

    public required GgufDeletionStageReceipt StageReceipt { get; init; }
}

public interface ILocalModelDeletionCoordinator
{
    Task<CommittedModelDeletion> CommitDeleteAsync(string modelName, CancellationToken cancellationToken = default);
    Task PurgeAfterSuccessAsync(CommittedModelDeletion committedDeletion, CancellationToken cancellationToken = default);
}

/// <summary>
///     Thrown when a base model cannot be deleted because installed LoRA adapters launch against it. An adapter carries no weights of its
///     own, so removing the base leaves every dependent adapter permanently unlaunchable.
/// </summary>
/// <remarks>
///     The global <c>ConflictExceptionHandler</c> turns it into a 409 with <c>conflictType = InstalledModelHasDependentAdapters</c> —
///     endpoints must let it propagate, never catch it.
/// </remarks>
public sealed class InstalledModelDependentAdaptersException : InvalidOperationException
{
    public InstalledModelDependentAdaptersException() : base("Installed LoRA adapters apply to this model. Remove them before deleting it.")
    {
    }
}

/// <summary>
///     Thrown when an alias of the model being deleted is mapped to a runtime provider other than llama.cpp, so the
///     GGUF deletion path is not the owner of that alias. Mapped to a 409 with
///     <c>conflictType = InstalledModelProviderConflict</c>.
/// </summary>
public sealed class InstalledModelProviderConflictException : InvalidOperationException
{
    public InstalledModelProviderConflictException() : base("The model is mapped to a different runtime provider. Refresh the model list and try again.")
    {
    }
}

/// <summary>
///     Thrown when a concurrent model mutation moved the provider map on past the revision this deletion (or its compensating rollback)
///     read, so the write would clobber someone else's change.
/// </summary>
/// <remarks>
///     Mapped to a 409 with <c>conflictType = InstalledModelProviderMapSuperseded</c>; the operation is retryable after a refresh.
/// </remarks>
public sealed class InstalledModelProviderMapSupersededException : InvalidOperationException
{
    public InstalledModelProviderMapSupersededException() : base("Another model change completed while this delete was running. Refresh the model list and try again.")
    {
    }
}

/// <summary>
///     Thrown when the model-management path is asked to perform an operation the model's runtime provider does not
///     own — today, deleting or pulling a model that lives on an operator-registered external endpoint.
/// </summary>
/// <remarks>
///     Mapped to a 409 with <c>conflictType</c> of <c>ModelOperationNotSupportedByProvider</c>. It exists because the refusal originates in
///     the PROVIDER assembly, which the host may not reference (the layer tests freeze that graph), and because 500 is the wrong answer for
///     a request that was understood perfectly and simply names the wrong lifecycle: external models are removed by unregistering them on
///     their connection.
/// </remarks>
public sealed class ModelOperationNotSupportedByProviderException : InvalidOperationException
{
    public ModelOperationNotSupportedByProviderException(string message)
        : base(message)
    {
    }

    public ModelOperationNotSupportedByProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
