namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The runner's output bounds. They exist because one caller feeds it MODEL-INFLUENCED bytes — the workflow tool
///     lane hands it a Development patch on stdin — and git echoes parts of a rejected patch back, so an unbounded read
///     of that output is the engine's memory in a hostile patch's hands. The trusted apply port has always read bounded;
///     this is the same discipline on the shared runner.
/// </summary>
public sealed class HostGitRunnerBoundsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-host-git-bounds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    /// <summary>
    ///     Output past the bound is answered as a failed run rather than accumulated — and rather than thrown, which is
    ///     this runner's whole contract: every failure it can have comes back as a non-zero exit with the reason on
    ///     stderr.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenOutputExceedsItsBound_AnswersAFailedRunInsteadOfAccumulatingIt()
    {
        var repository = await RepositoryAsync().ConfigureAwait(false);
        var runner = new HostGitRunner(timeoutSeconds: 30);

        var bounded = await runner.RunAsync(repository,
                                      AgentHomeGit.Arguments("status", "--porcelain=v1"),
                                      CancellationToken.None,
                                      standardInput: null,
                                      maxStandardOutputBytes: 4)
                                  .ConfigureAwait(false);

        AssertEx.Equal(expected: -1, bounded.ExitCode);
        AssertEx.Contains(bounded.StandardError, "more output than its configured bound");

        var unbounded = await runner.RunAsync(repository, AgentHomeGit.Arguments("status", "--porcelain=v1"), CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, unbounded.ExitCode, "the same command without a bound is untouched — this is opt-in per call.");
        AssertEx.Contains(unbounded.StandardOutput, "untracked.txt");
    }

    /// <summary>
    ///     A git that exits without reading its input is answered as a failed run, not as an exception out of the
    ///     write.
    ///     <para>
    ///         The input is bigger than a pipe buffer, so the write cannot complete until the reader consumes it — and
    ///         <c>--version</c> never does. The write therefore blocks, git exits, and the write comes back as a broken
    ///         pipe. Every failure this runner can have is a non-zero exit with a reason on stderr; this one used to be
    ///         the exception to that, thrown straight past callers written to the contract.
    ///     </para>
    ///     <para>
    ///         It is also what pins the ORDER of the drains against the stdin write. With the drains started after the
    ///         write, this call never reaches them at all.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RunAsync_WhenGitExitsWithoutReadingItsInput_AnswersAFailedRunInsteadOfThrowing()
    {
        var repository = await RepositoryAsync().ConfigureAwait(false);
        var runner = new HostGitRunner(timeoutSeconds: 30);

        // A megabyte: every pipe buffer this runs on is far smaller, so the write is still in flight when git exits.
        var result = await runner.RunAsync(repository,
                                     AgentHomeGit.Arguments("--version"),
                                     CancellationToken.None,
                                     standardInput: new byte[1024 * 1024])
                                 .ConfigureAwait(false);

        AssertEx.Equal(expected: -1, result.ExitCode, $"a git that never saw the input did not succeed at it: {result.StandardError}");
        AssertEx.Contains(result.StandardError, "stopped reading its input");
    }

    /// <summary>A repository with enough uncommitted noise for <c>status</c> to out-talk a four-character bound.</summary>
    private async Task<string> RepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repository);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(repository, "untracked.txt"), "noise\n").ConfigureAwait(false);
        return repository;
    }
}
