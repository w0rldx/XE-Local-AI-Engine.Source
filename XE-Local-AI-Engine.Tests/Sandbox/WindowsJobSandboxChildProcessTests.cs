namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The job child's exit and drain on any host (ADR 0025), through the fake native seam and a manual clock.</summary>
/// <remarks>The real overlapped-read cancellation is proven by <c>WindowsJobSandboxWindowsTests</c>.</remarks>
[Category(TestCategories.Unit)]
public sealed class WindowsJobSandboxChildProcessTests
{
    private static readonly WindowsJobStartInfo Command = new()
    {
        CommandLine = "tool.exe",
        Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        RedirectStandardInput = true
    };

    [Test]
    public async Task AStdoutHeldOutsideTheJob_EndsAtTheDrainBudget_DeliversThePendingLine_AndBothPumpsFinish()
    {
        // The root printed an unterminated line and exited with 3, its job was terminated, but a process outside the job still holds
        // stdout: no newline and no end of stream come.
        var api = new FakeWindowsJobApi { HoldStandardOutput = true, StandardOutputText = "partial", ExitCodeFor = static _ => 3 };
        var time = new ManualTimeProvider();
        var stdout = new List<string>();
        using var child = new WindowsJobSandboxChildProcess(new WindowsJobLauncher(api).Start(Command, Limits()),
            time,
            line =>
            {
                lock (stdout)
                {
                    stdout.Add(line);
                }
            },
            static _ => { });

        var wait = child.WaitForExitAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(wait, "the pumps get their drain budget before their reads are cancelled");
        time.Advance(MxcChildProcess.PumpDrainBudget);

        await AssertEx.CompletesAsync(wait, TestBudgets.Contended, "the wait must return once the drain budget is spent");
        AssertEx.Equal(3, await wait);
        lock (stdout)
        {
            AssertEx.Equal("partial", string.Join('|', stdout), "the unterminated line is delivered before the wait returns");
        }

        AssertEx.True(child.StandardOutputIncomplete, "a line cut off by the drain budget makes stdout incomplete");
        AssertEx.False(child.StandardErrorIncomplete, "stderr reached its end with nothing pending");
        using var pumps = new CancellationTokenSource(TestBudgets.Contended);
        AssertEx.True(await child.TryWaitForExitAsync(pumps.Token), "both pumps must end once their reads are cancelled");
    }

    [Test]
    public async Task TryWaitForExitAsync_TerminatesTheJob_OnceTheRootHasExited()
    {
        var api = new FakeWindowsJobApi { ExitCodeFor = static _ => 0 };
        using var child = new WindowsJobSandboxChildProcess(new WindowsJobLauncher(api).Start(Command, Limits()), TimeProvider.System, null, static _ => { });

        using var budget = new CancellationTokenSource(TestBudgets.Contended);
        AssertEx.True(await child.TryWaitForExitAsync(budget.Token));

        AssertEx.Equal(1, api.Terminations, "a member left running by the root must not hold the pumps open");
    }

    [Test]
    public async Task StandardInput_IsWrittenAsUtf8_WithoutAPreamble()
    {
        var api = new FakeWindowsJobApi { ExitCodeFor = static _ => 0 };
        using var child = new WindowsJobSandboxChildProcess(new WindowsJobLauncher(api).Start(Command, Limits()), TimeProvider.System, null, static _ => { });

        await child.WriteStandardInputAndCloseAsync("grüße", CancellationToken.None);

        using var written = new MemoryStream();
        await AssertEx.NotNull(api.LastStandardInput).Reader.CopyToAsync(written);
        // g r ü(C3 BC) ß(C3 9F) e, and no EF BB BF in front.
        AssertEx.Equal("6772C3BCC39F65", Convert.ToHexString(written.ToArray()));
    }

    private static WindowsJobLimits Limits() =>
        WindowsJobLimits.Map(2048, 64, null, 16);
}
