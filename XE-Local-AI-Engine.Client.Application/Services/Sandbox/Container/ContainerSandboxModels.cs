namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.Runtime.InteropServices;

/// <summary>
///     A Docker Engine API version as its two integer components. Docker reports these as decimal-looking strings that
///     are NOT decimals — 1.9 precedes 1.41 — so they are only ever compared component-wise.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct DockerApiVersion(int Major, int Minor);

/// <summary>One in-container mount target: the configuration property (or engine-generated mount) that names it, and the path.</summary>
internal sealed class ContainerMountTarget
{
    public required string Name { get; init; }

    public required string? Path { get; init; }
}

/// <summary>Two mount targets that shadow each other — equal paths, or one an ancestor of the other.</summary>
internal sealed class ContainerMountOverlap
{
    public required ContainerMountTarget First { get; init; }

    public required ContainerMountTarget Second { get; init; }
}
