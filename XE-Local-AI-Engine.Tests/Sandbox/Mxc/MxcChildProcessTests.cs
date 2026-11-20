namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Mxc.Sdk.V1;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
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
        process.When(p => p.Kill()).Do(_ => exit.TrySetResult(new WaitResult
        {
            ExitCode = -1
        }));
        using var child = new MxcChildProcess(process, TimeProvider.System);
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
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult
        {
            ExitCode = 0,
            TimedOut = true
        }, new WaitResult
        {
            ExitCode = 137
        });
        process.TryGetExitCode(out Arg.Any<int>()).Returns(false);
        using var child = new MxcChildProcess(process, TimeProvider.System);

        var exitCode = await child.WaitForExitAsync(CancellationToken.None);

        AssertEx.True(child.TimedOut);
        AssertEx.Equal(137, exitCode);
        process.Received(1).Kill();
    }

    [Test]
    public async Task WaitForExitAsync_OnNormalExit_ReturnsTheCodeWithoutKilling()
    {
        var process = Substitute.For<IMxcProcess>();
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult
        {
            ExitCode = 3
        });

        using var child = new MxcChildProcess(process, TimeProvider.System);

        AssertEx.Equal(3, await child.WaitForExitAsync(CancellationToken.None));
        AssertEx.False(child.TimedOut);
        process.DidNotReceive().Kill();
    }

    [Test]
    public async Task PumpedStreams_DeliverEveryLineBeforeTheWaitReturns_AndAreNotExposedRaw()
    {
        // Far larger than any pipe buffer: a child writing this much blocks unless both streams are drained concurrently.
        var stdoutText = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => "out-" + i.ToString(CultureInfo.InvariantCulture)));
        var process = Substitute.For<IMxcProcess>();
        process.StandardOutput.Returns(new MemoryStream(Encoding.UTF8.GetBytes(stdoutText)));
        process.StandardError.Returns(new MemoryStream(Encoding.UTF8.GetBytes("err-1\nerr-2\n")));
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult
        {
            ExitCode = 0
        });
        var stdout = new List<string>();
        var stderr = new List<string>();

        using var child = new MxcChildProcess(process, TimeProvider.System, stdout.Add, stderr.Add);
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
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult
        {
            ExitCode = 4
        });
        var time = new ManualTimeProvider();
        using var child = new MxcChildProcess(process, time, static _ => { });

        var wait = child.WaitForExitAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount > 0, TestBudgets.Contended, "the drain budget must be armed");
        await AssertEx.StaysIncompleteAsync(wait, "the wait gives the pump its drain budget before closing the stream");
        closer.DidNotReceive().Close();
        time.Advance(MxcChildProcess.PumpDrainBudget);

        await AssertEx.CompletesAsync(wait, TestBudgets.Contended, "the wait must return once the drain budget is spent");
        AssertEx.Equal(4, await wait);
        closer.Received(1).Close();
        AssertEx.True(child.StandardOutputIncomplete, "a stream closed under its pump at the deadline is cut, though the close read as end of stream");
    }

    [Test]
    public async Task WaitForExitAsync_WhenAClosedStreamStillNeverEnds_ReturnsAfterTheCloseGrace_AndFlagsTheStreamIncomplete()
    {
        // The close does not end the read (a closer that misses the native handle): the wait must still return.
        var stdout = new Pipe();
        var closer = Substitute.For<IMxcStreamCloser>();
        var process = Substitute.For<IMxcProcess>();
        process.StandardOutput.Returns(stdout.Reader.AsStream());
        process.StandardOutputCloser.Returns(closer);
        process.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult
        {
            ExitCode = 0
        });
        var time = new ManualTimeProvider();
        using var child = new MxcChildProcess(process, time, static _ => { });

        var wait = child.WaitForExitAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount > 0, TestBudgets.Contended, "the drain budget must be armed");
        await AssertEx.StaysIncompleteAsync(wait, "the wait gives the pump its drain budget before closing the stream");
        time.Advance(MxcChildProcess.PumpDrainBudget);
        // The close comes first, then the grace timer: advancing before it is armed would leave the wait on a deadline never reached.
        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount > 0, TestBudgets.Contended, "the close grace must be armed");
        await AssertEx.StaysIncompleteAsync(wait, "after the close the pump gets its grace to end");
        closer.Received(1).Close();
        time.Advance(MxcChildProcess.PumpCloseGrace);

        await AssertEx.CompletesAsync(wait, TestBudgets.Contended, "the wait must return once the close grace is spent");
        AssertEx.True(child.StandardOutputIncomplete, "a pump still reading when the wait returned leaves its capture incomplete");
        await stdout.Writer.CompleteAsync();
    }

    [Test]
    public async Task PumpLinesAsync_SplitsLikeReadLine_AndDeliversAFinalUnterminatedLine()
    {
        var lines = new List<string>();

        var incomplete = await MxcChildProcess.PumpLinesAsync(new MemoryStream("a\r\nb\rc\n\nd"u8.ToArray()), lines.Add, CancellationToken.None);

        AssertEx.Equal("a|b|c||d", string.Join('|', lines));
        AssertEx.False(incomplete, "every line reached its terminator or end of stream");
    }

    [Test]
    public async Task PumpLinesAsync_CutsALineWithoutNewlines_AtTheCaptureBudget_AndKeepsDraining()
    {
        // Twice the budget without a newline, then one more line: the pump holds at most the budget and still reads to the end.
        var text = new string('x', 2 * MxcChildProcess.MaxPendingLineChars) + "\nnext";
        var lines = new List<string>();

        var incomplete = await MxcChildProcess.PumpLinesAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), lines.Add, CancellationToken.None);

        AssertEx.Equal(2, lines.Count);
        AssertEx.Equal(MxcChildProcess.MaxPendingLineChars, lines[0].Length, "the newline-free run is delivered cut at the budget, never whole");
        AssertEx.Equal("next", lines[1], "the stream is drained past the cut line");
        AssertEx.True(incomplete, "a cut line makes the stream incomplete");
    }

    [Test]
    public async Task PumpLinesAsync_WhenTheReadFailsAfterACompleteLine_ReportsIncomplete()
    {
        var lines = new List<string>();
        await using var stream = new FailsAtEndStream("a\n"u8.ToArray());

        var incomplete = await MxcChildProcess.PumpLinesAsync(stream, lines.Add, CancellationToken.None);

        AssertEx.Equal("a", string.Join('|', lines));
        AssertEx.True(incomplete, "a read that failed before end of stream may have lost what followed");
    }

    [Test]
    public async Task PumpLinesAsync_WhenTheSdkReadFails_DeliversThePartialLine_AndReportsIncomplete()
    {
        var lines = new List<string>();
        await using var stream = new FailsAtEndStream("a\nparti"u8.ToArray(), new MxcException(ErrorCode.BackendError, "the pipe is closed"));

        var incomplete = await MxcChildProcess.PumpLinesAsync(stream, lines.Add, CancellationToken.None);

        AssertEx.Equal("a|parti", string.Join('|', lines), "the SDK's own read failure ends the pump like a broken pipe, never faults it");
        AssertEx.True(incomplete);
    }

    [Test]
    public async Task UnpumpedStreams_AreExposedRaw_AndIdIsTheOsPid()
    {
        var output = new MemoryStream("raw"u8.ToArray());
        var process = Substitute.For<IMxcProcess>();
        process.StandardOutput.Returns(output);
        process.Id.Returns(4242u);

        using var child = new MxcChildProcess(process, TimeProvider.System);

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

        new MxcChildProcess(process, TimeProvider.System).Dispose();

        process.Received(1).Dispose();
        await Task.CompletedTask;
    }

    /// <summary>Yields its bytes, then fails the next read as a broken pipe would, never reaching end of stream.</summary>
    private sealed class FailsAtEndStream : MemoryStream
    {
        private readonly Exception _failure;

        public FailsAtEndStream(byte[] bytes, Exception? failure = null)
            : base(bytes)
        {
            _failure = failure ?? new IOException("the pipe broke");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position < Length ? base.ReadAsync(buffer, cancellationToken) : ValueTask.FromException<int>(_failure);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
