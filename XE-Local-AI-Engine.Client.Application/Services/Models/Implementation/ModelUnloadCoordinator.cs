namespace XE_Local_AI_Engine.Client.Services.Models.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Gracefully evicts a model from llama-server's memory, in every role that holds it.</summary>
/// <remarks>
///     An in-flight generation completes before the model is evicted, so unload never interrupts a running turn.
/// </remarks>
public sealed class ModelUnloadCoordinator
{
    private readonly ILlamaServerProcessSupervisor _supervisor;

    public ModelUnloadCoordinator(ILlamaServerProcessSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        _supervisor = supervisor;
    }

    /// <summary>
    ///     Gracefully ejects every role-specific llama-server process for the model. The role set comes from
    ///     <see cref="Enum.GetValues{TEnum}" /> so a future <see cref="ModelRole" /> member is ejected too instead of silently staying
    ///     resident.
    /// </summary>
    /// <remarks>
    ///     Every role is an operator eject, even a role that is not running, so Background work cannot reload any of them.
    ///     A role that is not running reports <c>NotRunning</c>, which is success — the whole operation is idempotent. Only a process still
    ///     busy after the drain window (left running on purpose, because force is not set here) makes the unload unsuccessful.
    /// </remarks>
    /// <returns>
    ///     <see langword="false" /> only when a llama-server process was still busy when the bounded drain window elapsed and was left running.
    /// </returns>
    public async Task<bool> UnloadAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var unloaded = true;
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            var outcome = await _supervisor.EjectAsync(modelName, role, force: false, cancellationToken, operatorRequested: true);
            unloaded &= outcome is not LlamaServerEjectOutcome.TimedOutStillBusy;
        }

        return unloaded;
    }
}
