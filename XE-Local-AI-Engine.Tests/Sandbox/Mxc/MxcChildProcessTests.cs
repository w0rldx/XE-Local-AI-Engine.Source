namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.IO.Pipelines;
using System.Text;
using Microsoft.Mxc.Sdk.V1;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="MxcChildProcess" /> over a substituted <see cref="IMxcProcess" />: MXC's cancelled wait leaves the child running, so
///     cancellation and a reported timeout must kill and then wait without a token.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class MxcChildProcessTests
{
    [Test]
    public async Task WaitForExitAsync_WhenCancelled_KillsThenWaitsUncancellably_ThenThrows()
    {
        var exit = new TaskCompletionSource<WaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = Substitute.For<IMxcProcess>();
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(call => exit.Task.WaitAsync(call.Arg<CancellationToken>()));
        process.TryGetExitCode(out Arg.Any<int>()).Returns(false);
        process.When(p => p.Kill()).Do(_ => exit.TrySetResult(new WaitResult { ExitCode = -1 }));
        using var child = new MxcChildProcess(process);
        using var cancellation = new CancellationTokenSource();

        var wait = child.WaitForExitAsync(cancellation.Token);
        await AssertEx.StaysIncompleteAsync(wait, "the wait must not finish before the child exits");
        await cancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => wait);
        Received.InOrder(() =>
        {
            process.WaitAsync(cancellation.Token);
            process.Kill();
            process.WaitAsync(CancellationToken.None);
        });
    }

    [Test]
    public async Task WaitForExitAsync_WhenMxcReportsTimedOut_KillsAndFlagsIt()
    {
        var process = Substitute.For<IMxcProcess>();
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult { ExitCode = 0, TimedOut = true }, new WaitResult { ExitCode = 137 });
        process.TryGetExitCode(out Arg.Any<int>()).Returns(false);
        using var child = new MxcChildProcess(process);

        var exitCode = await child.WaitForExitAsync(CancellationToken.None);

        AssertEx.True(child.TimedOut);
        AssertEx.Equal(137, exitCode);
        process.Received(1).Kill();
    }

    [Test]
    public async Task WaitForExitAsync_OnNormalExit_ReturnsTheCodeWithoutKilling()
    {
        var process = Substitute.For<IMxcProcess>();
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult { ExitCode = 3 });

        using var child = new MxcChildProcess(process);

        AssertEx.Equal(3, await child.WaitForExitAsync(CancellationToken.None));
        AssertEx.False(child.TimedOut);
        process.DidNotReceive().Kill();
    }

    [Test]
    public async Task PumpedStreams_DeliverEveryLineBeforeTheWaitReturns_AndAreNotExposedRaw()
    {
        // Far larger than any pipe buffer: a child writing this much blocks unless both streams are drained concurrently.
        var stdoutText = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => "out-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var process = Substitute.For<IMxcProcess>();
        process.StandardOutput.Returns(new MemoryStream(Encoding.UTF8.GetBytes(stdoutText)));
        process.StandardError.Returns(new MemoryStream(Encoding.UTF8.GetBytes("err-1\nerr-2\n")));
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult { ExitCode = 0 });
        var stdout = new List<string>();
        var stderr = new List<string>();

        using var child = new MxcChildProcess(process, stdout.Add, stderr.Add);
        await child.WaitForExitAsync(CancellationToken.None);

        AssertEx.Null(child.StandardOutput);
        AssertEx.Null(child.StandardError);
        AssertEx.Equal(20_000, stdout.Count);
        AssertEx.Equal("out-19999", stdout[^1]);
        AssertEx.Equal(2, stderr.Count);
    }

    [Test]
    public async Task WaitForExitAsync_WhenAPumpedStreamNeverEnds_ClosesItAfterTheDrainBudget_AndReturns()
    {
        // A grandchild that inherited stdout keeps the pipe open after the child exits: nothing is ever written and EOF never comes.
        var stdout = new Pipe();
        var closer = Substitute.For<IMxcStreamCloser>();
        closer.When(c => c.Close()).Do(_ => stdout.Writer.Complete());
        var process = Substitute.For<IMxcProcess>();
        process.StandardOutput.Returns(stdout.Reader.AsStream());
        process.StandardOutputCloser.Returns(closer);
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult { ExitCode = 4 });
        var time = new ManualTimeProvider();
        using var child = new MxcChildProcess(process, static _ => { }, timeProvider: time);

        var wait = child.WaitForExitAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(wait, "the wait gives the pump its drain budget before closing the stream");
        closer.DidNotReceive().Close();
        time.Advance(MxcChildProcess.PumpDrainBudget);

        await AssertEx.CompletesAsync(wait, TestBudgets.Contended, "the wait must return once the drain budget is spent");
        AssertEx.Equal(4, await wait);
        closer.Received(1).Close();
    }

    [Test]
    public async Task UnpumpedStreams_AreExposedRaw_AndIdIsTheOsPid()
    {
        var output = new MemoryStream("raw"u8.ToArray());
        var process = Substitute.For<IMxcProcess>();
        process.StandardOutput.Returns(output);
        process.Id.Returns(4242u);

        using var child = new MxcChildProcess(process);

        AssertEx.True(child.StandardOutput is MxcCancellableStdoutStream);
        using (var reader = new StreamReader(child.StandardOutput!, Encoding.UTF8, leaveOpen: true))
        {
            AssertEx.Equal("raw", await reader.ReadToEndAsync());
        }

        AssertEx.Equal(4242, child.Id);
    }

    [Test]
    public async Task Dispose_DisposesTheMxcProcess_WhichKillsARunningChild()
    {
        var process = Substitute.For<IMxcProcess>();

        new MxcChildProcess(process).Dispose();

        process.Received(1).Dispose();
        await Task.CompletedTask;
    }
}
