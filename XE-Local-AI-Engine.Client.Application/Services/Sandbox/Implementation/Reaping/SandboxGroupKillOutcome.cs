namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

/// <summary>What one attempt to kill a sandbox child's process group proved.</summary>
/// <remarks>Only <see cref="Deferred" /> keeps state: the orphan marker, and in the startup sweep the jail it names.</remarks>
public enum SandboxGroupKillOutcome
{
    /// <summary>The kill was delivered, or the group emptied after it.</summary>
    Killed,

    /// <summary>No member of the group was left to signal.</summary>
    Gone,

    /// <summary>The pid now names another process, so nothing was signalled.</summary>
    Mismatch,

    /// <summary>Delivery or identity could not be verified, so members may remain; nothing unverified was signalled.</summary>
    Deferred
}
