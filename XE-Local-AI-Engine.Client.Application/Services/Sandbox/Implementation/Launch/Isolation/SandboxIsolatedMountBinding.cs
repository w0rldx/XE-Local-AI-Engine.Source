namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>One engine-generated mount as an already-opened descriptor, its in-sandbox target and its write posture.</summary>
internal sealed class SandboxIsolatedMountBinding
{
    public required int FileDescriptor { get; init; }

    public required string SandboxPath { get; init; }

    public required bool ReadOnly { get; init; }
}
