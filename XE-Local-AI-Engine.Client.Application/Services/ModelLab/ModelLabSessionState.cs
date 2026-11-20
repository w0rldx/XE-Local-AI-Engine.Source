namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Where a <see cref="ModelLabGpuSession" /> is. Only <see cref="Idle" /> is preempted; demand in the two before it is latched.</summary>
internal enum ModelLabSessionState
{
    /// <summary>Taking the gate, the lease and the reservation for a cold start.</summary>
    Admitting,

    /// <summary>A job borrows the session and its worker.</summary>
    Active,

    /// <summary>A worker holds the model and the GPU with no job, until its idle time or the next demand.</summary>
    Idle,

    /// <summary>The worker is being stopped and the holds given back; nothing reuses it.</summary>
    Evicting,

    /// <summary>Everything is given back, or handed to a steering job's arms.</summary>
    Ended
}
