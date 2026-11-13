namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Applies a stored Development patch to a freshly cloned base on the host, so whatever judges it judges the base
///     plus exactly that patch. Used by the Dev Mode validation tree.
/// </summary>
/// <remarks>
///     <c>--index</c> stages what it applies without rewriting a byte, and whitespace errors are applied as is: the
///     gate's own <c>git diff --check</c> is what reports them back to the coder.
/// </remarks>
internal static class DevelopmentPatchOverlay
{
    public static Task<HostGitResult> ApplyAsync(string worktreePath,
        ReadOnlyMemory<byte> patch,
        DevelopmentOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(options);
        return new HostGitRunner(options.MaxAttemptDurationSeconds)
            .RunAsync(worktreePath,
                AgentHomeGit.Arguments("apply", "--index", "--whitespace=nowarn", "-"),
                cancellationToken,
                patch,
                options.MaxPatchBytes,
                options.MaxCommandOutputBytes);
    }
}
