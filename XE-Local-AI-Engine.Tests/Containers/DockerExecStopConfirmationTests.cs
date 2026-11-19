namespace XE_Local_AI_Engine.Tests.Containers;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     How the client reads the Docker stop scripts: the stop script's exit codes, where a run that still finds processes is
///     retried rather than final (an unconfirmed stop kills the sandbox), and the wrapper's line for a command it cannot start.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DockerExecStopConfirmationTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    [Test]
    public async Task AScriptRunThatStillFoundProcesses_IsRerunAfterAPause_AndASecondCleanRunConfirms()
    {
        var clock = new ManualTimeProvider();
        var exitCodes = new Queue<long>([1, 0]);
        var runs = 0;

        var confirm = DockerDotNetRuntimeClient.ConfirmStopAsync(_ =>
            {
                runs++;
                return Task.FromResult(exitCodes.Dequeue());
            },
            static _ => Task.FromResult(false),
            clock,
            CancellationToken.None);

        AssertEx.False(confirm.IsCompleted, "a run that still found processes must not be final");
        AssertEx.Equal(expected: 1, runs, "the rerun waits for the pause");
        clock.Advance(PollInterval);
        AssertEx.True(await confirm, "the second, clean run confirms the stop");
        AssertEx.Equal(expected: 2, runs);
    }

    [Test]
    public async Task RunsThatKeepFindingProcesses_EndWhenTheDeadlineCancels()
    {
        var clock = new ManualTimeProvider();
        using var deadline = new CancellationTokenSource();

        var confirm = DockerDotNetRuntimeClient.ConfirmStopAsync(static _ => Task.FromResult(1L), static _ => Task.FromResult(false), clock, deadline.Token);
        clock.Advance(PollInterval);
        clock.Advance(PollInterval);
        await deadline.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => confirm);
    }

    [Test]
    public async Task NoIdentityYet_ConfirmsOnceTheDaemonReportsTheWrapperOver()
    {
        var clock = new ManualTimeProvider();
        var running = new Queue<bool>([true, false]);

        var confirm = DockerDotNetRuntimeClient.ConfirmStopAsync(static _ => Task.FromResult(3L), _ => Task.FromResult(running.Dequeue()), clock, CancellationToken.None);
        AssertEx.False(confirm.IsCompleted, "the wrapper still runs, so the stop is not confirmed yet");
        clock.Advance(PollInterval);

        AssertEx.True(await confirm);
    }

    [Test]
    public async Task AnyOtherExitCode_IsUnconfirmed()
    {
        AssertEx.False(await DockerDotNetRuntimeClient.ConfirmStopAsync(static _ => Task.FromResult(126L),
            static _ => Task.FromResult(false),
            new ManualTimeProvider(),
            CancellationToken.None));
    }

    [Test]
    [Arguments("path", "./tool", 127L, "executable file not found in $PATH")]
    [Arguments("missing", "./tool", 127L, "stat ./tool: no such file or directory")]
    [Arguments("denied", "/work/tool", 126L, "permission denied")]
    [Arguments("directory", "/work", 126L, "is a directory: permission denied")]
    public async Task AWrapperReasonLine_ReadsAsTheDaemonsLaunchFailure(string reason, string executable, long exitCode, string daemonReason)
    {
        const string IdentityPath = "/tmp/.xe-exec-0123456789abcdef0123456789abcdef";

        var outcome = AssertEx.NotNull(DockerDotNetRuntimeClient.DescribeLaunchFailureOrNull(IdentityPath + " " + reason + "\n", IdentityPath, executable));

        AssertEx.Equal(exitCode, outcome.ExitCode);
        AssertEx.Equal($"OCI runtime exec failed: exec failed: unable to start container process: exec: \"{executable}\": {daemonReason}\r\n", outcome.StandardOutput);
        AssertEx.Empty(outcome.StandardError);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("/tmp/.xe-exec-0123456789abcdef0123456789abcdef path")]
    [Arguments("/tmp/.xe-exec-ffffffffffffffffffffffffffffffff path\n")]
    [Arguments("/tmp/.xe-exec-0123456789abcdef0123456789abcdef other\n")]
    [Arguments("sh: tool: not found\n")]
    public async Task AnythingElse_IsTheCommandsOwnOutput(string standardError)
    {
        AssertEx.Null(DockerDotNetRuntimeClient.DescribeLaunchFailureOrNull(standardError, "/tmp/.xe-exec-0123456789abcdef0123456789abcdef", "tool"));
        await Task.CompletedTask;
    }

    private static readonly string[] ProcessTitles = ["PID", "SID", "STAT", "STARTED"];

    [Test]
    public async Task AKnownSurvivor_IsAccountedFor_AndAPidRecycledWithAnotherStartTime_IsNot()
    {
        var known = new ConcurrentDictionary<long, string>
        {
            [500] = "Thu Nov 19 10:00:00 2026",
            [600] = "Thu Nov 19 10:00:01 2026",
            [700] = "Thu Nov 19 10:00:02 2026"
        };
        IList<IList<string>> rows =
        [
            ["100", "100", "Ss", "Thu Nov 19 09:00:00 2026"],
            ["500", "480", "S", "Thu Nov 19 10:00:00 2026"],
            ["600", "600", "Ss", "Thu Nov 19 10:05:00 2026"],
            ["800", "800", "Z", "Thu Nov 19 10:06:00 2026"]
        ];

        var unaccounted = DockerDotNetRuntimeClient.SelectUnaccounted(ProcessTitles, rows, new HashSet<long> { 100 }, known);

        AssertEx.Equal(expected: 600, unaccounted!.Single().Pid, "pid 600 now names a process started later, so it is not the survivor");
        AssertEx.Equal(expected: 500, known.Keys.Single(), "the recycled pid and the vanished one are pruned; the live survivor stays");
        await Task.CompletedTask;
    }

    [Test]
    public async Task AProcessListWithoutAStartTime_CannotBeAccountedFor()
    {
        IList<IList<string>> rows = [["100", "100", "Ss"]];

        AssertEx.Null(DockerDotNetRuntimeClient.SelectUnaccounted(["PID", "SID", "STAT"], rows, new HashSet<long> { 100 }, new ConcurrentDictionary<long, string>()));
        await Task.CompletedTask;
    }
}
