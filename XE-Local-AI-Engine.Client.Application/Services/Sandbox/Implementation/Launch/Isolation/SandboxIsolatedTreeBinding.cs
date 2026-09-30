namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     One read-only tree the jail must see, as an already-opened descriptor plus the canonical path it is bound at.
/// </summary>
internal sealed class SandboxIsolatedTreeBinding
{
    public required int FileDescriptor { get; init; }

    public required string Path { get; init; }
}
