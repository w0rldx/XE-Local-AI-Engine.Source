namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>What an eject of the warm Model Lab worker achieved.</summary>
public enum ModelLabEjectOutcome
{
    /// <summary>No worker is left: it was seen to exit, or nothing was warm.</summary>
    Gone,

    /// <summary>A job holds the worker (Starting or Active); nothing was done.</summary>
    Busy,

    /// <summary>A worker was stopped but its exit was not seen, so it may still hold GPU memory, and starts are refused until a reap proves it gone.</summary>
    NotConfirmed
}
