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
