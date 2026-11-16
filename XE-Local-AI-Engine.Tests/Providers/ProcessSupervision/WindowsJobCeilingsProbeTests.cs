namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The plain-path probe's verdicts (ADR 0025), through the fake native seam: only every control behaving advertises the ceilings, and
///     each control that does not is unsupported with its reason, never a silent downgrade.
/// </summary>
[Category(TestCategories.Unit)]
public sealed partial class WindowsJobCeilingsProbeTests
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";

    [Test]
    public async Task EveryControlBehaving_IsSupported_WithTheProcessorCount_AndLeavesNoHandle()
    {
        var api = HonestHost();

        var result = WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd);

        AssertEx.True(result.Supported, result.Reason);
        AssertEx.Null(result.Reason);
        AssertEx.Equal(16, result.SystemProcessorCount);
        AssertEx.Equal(4, api.Started.Count);
        // Every control runs under the probe's own small job, the CPU rate included, and through the one launcher.
        AssertEx.Equal(2, api.Calls.Count(static call => call == "TryCommit"));
        AssertEx.Equal(4, api.Calls.Count(static call => call == "SetCpuRate"));
        AssertEx.Equal(0, api.OpenHandles);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ACommitInsideTheLimitFailing_IsUnsupported()
    {
        var api = HonestHost();
        api.Commit = static _ => 8;

        AssertUnsupported(WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd), "commit inside a 256 MB job failed (error 8)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ACommitPastTheLimitSucceeding_IsUnsupported()
    {
        var api = HonestHost();
        api.Commit = static _ => 0;

        AssertUnsupported(WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd), "the job memory limit is not enforced");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ACommitPastTheLimitFailingForAnotherReason_IsUnsupported()
    {
        var api = HonestHost();
        api.Commit = static bytes => bytes < 100UL * 1_048_576 ? 0 : 5;

        AssertUnsupported(WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd), "not ERROR_COMMITMENT_LIMIT");
        await Task.CompletedTask;
    }

    [Test]
    public async Task AChainInsideTheCapThatCannotStart_IsUnsupported()
    {
        var api = HonestHost();
        api.ExitCodeUnderCap = static (start, _) => IsHolder(start) ? null : 1;

        AssertUnsupported(WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd), "a chain of 2 processes inside a 4-process job exited 1");
        await Task.CompletedTask;
    }

    [Test]
    public async Task APastCapChainThatCannotRunUnderAWiderCapEither_IsUnsupported()
    {
        // The five-deep chain fails whatever the cap: its failure under four proves nothing about the cap.
        var api = HonestHost();
        api.ExitCodeUnderCap = static (start, _) => IsHolder(start) ? null : ShallowOnly(start);

        AssertUnsupported(WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd), "a chain of 5 processes inside a 8-process job exited 1");
        await Task.CompletedTask;
    }

    [Test]
    public async Task AChainPastTheCapThatRuns_IsUnsupported()
    {
        var api = HonestHost();
        api.ExitCodeUnderCap = static (start, _) => IsHolder(start) ? null : 7;

        AssertUnsupported(WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd), "the active-process limit is not enforced");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("CreateJob")]
    [Arguments("SetCpuRate")]
    [Arguments("CreateSuspended")]
    public async Task ALauncherFailure_IsUnsupported_WithItsMessage_AndLeavesNoHandle(string step)
    {
        var api = HonestHost();
        api.FailAt = step;

        var result = WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd);

        AssertUnsupported(result, $"injected failure at {step}");
        AssertEx.Equal(0, api.OpenHandles);
        await Task.CompletedTask;
    }

    [Test]
    public async Task AReadBackMismatch_IsUnsupported()
    {
        var api = HonestHost();
        api.ReadBack = static limits => limits with { CpuRate = 0 };

        AssertUnsupported(WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd), "read back");
        await Task.CompletedTask;
    }

    [Test]
    public async Task NoProcessorCount_IsUnsupported()
    {
        var api = HonestHost();
        api.Processors = 0;

        AssertUnsupported(WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), Cmd), "processor count could not be read");
        await Task.CompletedTask;
    }

    // A host whose job behaves: the commit holder runs until terminated, a commit past 256 MB fails with ERROR_COMMITMENT_LIMIT, and a
    // cmd chain exits 7 exactly when its depth fits the 4-process cap.
    private static FakeWindowsJobApi HonestHost() =>
        new()
        {
            Commit = static bytes => bytes <= 256UL * 1_048_576 ? 0 : 1455,
            ExitCodeUnderCap = static (start, cap) => IsHolder(start) ? null : ChainExit(start, cap)
        };

    // A chain runs to its exit code exactly when its depth fits the job's cap.
    private static int ChainExit(WindowsJobStartInfo start, uint cap) =>
        ChainDepth().Count(start.CommandLine) <= cap ? 7 : 1;

    private static int ShallowOnly(WindowsJobStartInfo start) =>
        ChainDepth().Count(start.CommandLine) <= 2 ? 7 : 1;

    private static bool IsHolder(WindowsJobStartInfo start) =>
        start.CommandLine.EndsWith("/k", StringComparison.Ordinal);

    private static void AssertUnsupported(WindowsJobProbeResult result, string reason)
    {
        AssertEx.False(result.Supported);
        AssertEx.Contains(result.Reason, "the Windows Job Object ceilings probe failed");
        AssertEx.Contains(result.Reason, reason);
    }

    // Each "/d /c" starts one more cmd process.
    [GeneratedRegex(@"/d /c", RegexOptions.CultureInvariant)]
    private static partial Regex ChainDepth();
}
