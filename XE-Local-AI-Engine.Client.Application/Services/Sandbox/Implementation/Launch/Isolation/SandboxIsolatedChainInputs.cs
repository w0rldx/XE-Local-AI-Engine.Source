namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     Everything the isolated chain needs that is NOT a decision: resolved helper paths, descriptor numbers, the unit name, the host's
///     filesystem layout, and the ceilings.
/// </summary>
/// <remarks>
///     Every field is already measured or already opened, which is what lets <see cref="SandboxIsolatedChain.Render" /> stay a pure
///     function of its input.
/// </remarks>
internal sealed record SandboxIsolatedChainInputs
{
    public required string SetsidPath { get; init; }

    public required string SystemdRunPath { get; init; }

    public required string BwrapPath { get; init; }

    /// <summary>The transient scope's unit name — the handle every later kill and every orphan sweep works through.</summary>
    public required string ScopeUnitName { get; init; }

    /// <summary>Wall-clock ceiling the user manager enforces on the scope, independent of the engine being alive.</summary>
    public required long RuntimeMaxSeconds { get; init; }

    public required uint UserId { get; init; }

    public required uint GroupId { get; init; }

    public required IReadOnlyList<SandboxUsrMergeEntry> UsrMergeEntries { get; init; }

    public required int PasswdDescriptor { get; init; }

    public required int GroupDescriptor { get; init; }

    public required int NameServiceSwitchDescriptor { get; init; }

    public required int HostsDescriptor { get; init; }

    public required int JailDescriptor { get; init; }

    public required int JailTempDescriptor { get; init; }

    public IReadOnlyList<SandboxIsolatedTreeBinding> ReadOnlyTrees { get; init; } = [];

    public SandboxResourceLimits? ResourceLimits { get; init; }

    /// <summary>The value every numeric-library thread-count variable is pinned to inside the jail.</summary>
    public required int ThreadLimit { get; init; }

    /// <summary>
    ///     The command's working directory INSIDE the sandbox. Always <c>/work</c> or a path beneath it — the jail is
    ///     the only writable tree and the only place a caller's requested subdirectory can be.
    /// </summary>
    public string WorkingDirectory { get; init; } = SandboxIsolatedChain.WorkPath;

    /// <summary>Variables the CALLER asked the command to run with, emitted after the fixed allow-list so they override it.</summary>
    /// <remarks>
    ///     The chain is <c>--clearenv</c> plus an allow-list, so a caller's <c>SandboxCommandRequest.Environment</c>, which the
    ///     non-isolated path honours, would otherwise be silently dropped the moment a sandbox opted into isolation. A variable that
    ///     quietly stops arriving is a far worse failure than one that is refused.
    /// </remarks>
    public IReadOnlyDictionary<string, string> AdditionalEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
