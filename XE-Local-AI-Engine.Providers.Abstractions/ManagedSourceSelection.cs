namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Which repository a managed source build compiles from.</summary>
public enum ManagedSourceSelection
{
    /// <summary>The runtime's official repository at the engine-pinned revision.</summary>
    Official = 0,

    /// <summary>An operator-supplied public GitHub repository, which requires an explicit risk acknowledgement.</summary>
    Custom = 1
}
