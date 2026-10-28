namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal sealed class DevelopmentWorkspaceSession
{
    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid AttemptId { get; init; }

    public required string BaseCommit { get; init; }

    public required string RepositoryIdentityHash { get; init; }

    public required string HostWorktreePath { get; init; }

    public required string RuntimePath { get; init; }

    /// <summary>The operator checkout's Git common directory, whose objects the evidence stage reads.</summary>
    /// <remarks>Resolved by the provider from the trusted repository root, never from anything inside the worktree.</remarks>
    public required string TrustedGitDirectory { get; init; }

    /// <summary>The trusted checkout's object format, which every repository the engine creates for the task shares.</summary>
    public string ObjectFormat { get; init; } = "sha1";

    /// <summary>The host directory holding the sandbox's home, tmp and dotnet directories; <see cref="RuntimePath" /> when null.</summary>
    /// <remarks>Set where the coder's own must not be read: validation, review and the warm restore get fresh ones.</remarks>
    public string? ScratchPath { get; init; }

    public required SandboxHandle SandboxHandle { get; init; }

    /// <summary>
    ///     The <c>PATH</c> commands get inside a filesystem boundary, naming the host toolchain trees bound read-only; <see langword="null" />
    ///     when the sandbox has none and commands inherit the host's.
    /// </summary>
    public string? SandboxSearchPath { get; init; }

    /// <summary>The <c>DOTNET_ROOT</c> commands get inside a filesystem boundary (<see cref="Implementation.DevelopmentSandboxToolchain.DotnetRoot" />).</summary>
    public string? SandboxDotnetRoot { get; init; }

    /// <summary>
    ///     The toolchain trees a <c>PATH</c> executable needed that the boundary could not bind, with why: a runner the commands then
    ///     miss may well be installed on the host. Empty without a boundary.
    /// </summary>
    public IReadOnlyList<DevelopmentSkippedToolchainTree> UnreachableToolchain { get; init; } = [];
}

internal interface IDevelopmentWorkspaceProvider
{
    Task<DevelopmentWorkspaceSession> PrepareAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     A fresh tree of <paramref name="workspace" />'s base commit plus <paramref name="patch" />, with fresh home, tmp
    ///     and dotnet directories and a sandbox of its own that the caller kills, for validation and the reviewer's tools.
    /// </summary>
    Task<DevelopmentWorkspaceSession> PrepareValidationAsync(DevelopmentWorkspaceSession workspace,
        DevelopmentRepositoryBinding repository,
        ReadOnlyMemory<byte> patch,
        CancellationToken cancellationToken = default);
}
