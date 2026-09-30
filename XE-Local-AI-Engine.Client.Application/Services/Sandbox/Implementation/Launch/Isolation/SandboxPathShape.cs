namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>What the host filesystem says about one legacy root; the seam the layout algorithm is tested through.</summary>
internal sealed class SandboxPathShape
{
    public required bool Exists { get; init; }

    public required bool IsSymbolicLink { get; init; }

    public required bool IsDirectory { get; init; }

    public required string? CanonicalPath { get; init; }
}
