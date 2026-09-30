namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>How one legacy top-level directory is reproduced inside the jail.</summary>
internal enum SandboxUsrMergeAction
{
    /// <summary>A symlink into the single read-only <c>/usr</c> bind — the usr-merged case.</summary>
    Symlink,

    /// <summary>A read-only bind of a real directory that is NOT part of <c>/usr</c> — the split-usr case.</summary>
    ReadOnlyBind
}

/// <summary>One legacy root and how the chain will reproduce it. <see cref="Target" /> is set only for a symlink.</summary>
internal sealed class SandboxUsrMergeEntry
{
    public required string Path { get; init; }

    public required SandboxUsrMergeAction Action { get; init; }

    public required string? Target { get; init; }
}
