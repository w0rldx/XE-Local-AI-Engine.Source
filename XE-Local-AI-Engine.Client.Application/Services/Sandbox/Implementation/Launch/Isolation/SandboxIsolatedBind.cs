namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>One engine-generated mount the isolated chain binds at a path of its choosing, writable or read-only.</summary>
/// <remarks>
///     The isolated form of a <see cref="SandboxMount" />: the registry resolves the in-sandbox target once at create time, so the launch
///     path only opens <see cref="HostPath" /> and binds it at <see cref="SandboxPath" />.
/// </remarks>
internal sealed record SandboxIsolatedBind
{
    /// <summary>The canonical host directory or regular file.</summary>
    public required string HostPath { get; init; }

    /// <summary>The absolute in-sandbox target.</summary>
    public required string SandboxPath { get; init; }

    public required bool ReadOnly { get; init; }

    /// <summary>Whether <see cref="HostPath" /> is a regular file rather than a directory.</summary>
    public required bool IsFile { get; init; }
}
