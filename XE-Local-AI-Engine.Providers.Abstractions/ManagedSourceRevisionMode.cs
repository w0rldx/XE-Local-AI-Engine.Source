namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Which revision of the selected repository a managed source build compiles.</summary>
public enum ManagedSourceRevisionMode
{
    /// <summary>The revision this engine pins for the runtime.</summary>
    EnginePinned = 0,

    /// <summary>Whatever the repository's default branch points at when the build starts.</summary>
    DefaultBranch = 1,

    /// <summary>An operator-supplied full 40-character commit SHA.</summary>
    ExplicitCommit = 2
}
