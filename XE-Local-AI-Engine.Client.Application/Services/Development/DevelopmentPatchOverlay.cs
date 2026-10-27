namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Applies a stored Development patch to a freshly cloned base on the host, so whatever judges it judges the base
///     plus exactly that patch. Shared by the Dev Mode validation tree and the Dev Workflows overlay.
/// </summary>
/// <remarks>
///     <c>--index</c> stages what it applies, and neither mode rewrites a byte. <c>refuseWhitespaceErrors</c>
///     is the Dev Workflows overlay's: it judges an approved patch and refuses one git would complain about. The Dev
///     Mode gate applies as is, because its own <c>git diff --check</c> is what reports the error back to the coder.
/// </remarks>
internal static class DevelopmentPatchOverlay
{
    public static Task<HostGitResult> ApplyAsync(string worktreePath,
        ReadOnlyMemory<byte> patch,
        DevelopmentOptions options,
        bool refuseWhitespaceErrors,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(options);
        return new HostGitRunner(options.MaxAttemptDurationSeconds)
            .RunAsync(worktreePath,
                AgentHomeGit.Arguments("apply", "--index", refuseWhitespaceErrors ? "--whitespace=error-all" : "--whitespace=nowarn", "-"),
                cancellationToken,
                patch,
                options.MaxPatchBytes,
                options.MaxCommandOutputBytes);
    }
}
