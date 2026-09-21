namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Removes one AgentHome run directory on an operator's say-so.
/// </summary>
/// <remarks>
///     Its own seam rather than a method on <see cref="IAgentHomeRunListService" />: that interface's contract is that
///     it only reads the runs directory, and a delete hanging off it would make the name untrue for every caller. The
///     retention sweep remains the only other thing that deletes a run, and both pass the same gates — the run is one
///     the node minted, it resolves under the runs root, it is not a link, and the execution lease is not held when
///     the removal happens.
/// </remarks>
public interface IAgentHomeRunDeleteService
{
    Task<AgentHomeRunDeleteOutcome> DeleteAsync(string runId, CancellationToken cancellationToken = default);
}

/// <summary>What a delete attempt settled on.</summary>
public enum AgentHomeRunDeleteOutcome
{
    /// <summary>The run directory is gone.</summary>
    Deleted,

    /// <summary>
    ///     No such run. Also the answer for a name the node never minted, one that resolves outside the runs root, and
    ///     a linked directory — the caller must not be able to tell those apart from a run that simply is not there.
    /// </summary>
    NotFound,

    /// <summary>
    ///     Not right now: a run holds the execution lease, or the removal itself failed. One outcome for both because
    ///     the operator's next move is the same — wait and try again — and naming the disk error would say where the
    ///     run sits.
    /// </summary>
    Conflict
}
