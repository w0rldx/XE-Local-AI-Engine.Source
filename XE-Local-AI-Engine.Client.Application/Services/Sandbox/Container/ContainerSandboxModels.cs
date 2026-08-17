namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     A Docker Engine API version as its two integer components. Docker reports these as decimal-looking strings that
///     are NOT decimals — 1.9 precedes 1.41 — so they are only ever compared component-wise.
/// </summary>
internal readonly record struct DockerApiVersion(int Major, int Minor);

/// <summary>One in-container mount target: the configuration property (or engine-generated mount) that names it, and the path.</summary>
internal sealed record ContainerMountTarget(string Name, string? Path);

/// <summary>Two mount targets that shadow each other — equal paths, or one an ancestor of the other.</summary>
internal sealed record ContainerMountOverlap(ContainerMountTarget First, ContainerMountTarget Second);
