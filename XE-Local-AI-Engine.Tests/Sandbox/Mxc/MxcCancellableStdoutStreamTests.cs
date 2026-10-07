namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Text;
using Microsoft.Mxc.Sdk.V1;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="MxcCancellableStdoutStream" />: the SDK's stdout read ignores its token, so a cancelled read must close the stream
///     through the SDK's closer and return instead of waiting for the child to write.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class MxcCancellableStdoutStreamTests
{
    [Test]
    public async Task ReadAsync_WhenCancelledMidRead_ClosesStdoutThroughTheCloser_AndThrowsCancelled()
    {
        using var inner = new IdleStdout();
        var closer = Substitute.For<IMxcStreamCloser>();
        closer.When(c => c.Close()).Do(_ => inner.Release());
        using var stream = new MxcCancellableStdoutStream(inner, closer);
        using var reader = new CancellationTokenSource();

        var read = stream.ReadAsync(new byte[16], reader.Token).AsTask();
        await inner.Reading;
        await reader.CancelAsync();

        // A TimeoutException here means the cancelled read stayed parked in the SDK's synchronous read.
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TestBudgets.Contended));
        closer.Received(1).Close();
    }

    [Test]
    public async Task ReadAsync_WhenCancelled_TheAbandonedReadNeverWritesTheCallersBuffer()
    {
        // The MCP SDK rents its read buffers: once a read was cancelled the buffer is back in the pool and someone else's.
        using var inner = new IdleStdout { LateBytes = [0xEE, 0xEE, 0xEE, 0xEE] };
        using var stream = new MxcCancellableStdoutStream(inner, Substitute.For<IMxcStreamCloser>());
        using var reader = new CancellationTokenSource();
        var buffer = new byte[4];

        var read = stream.ReadAsync(buffer, reader.Token).AsTask();
        await inner.Reading;
        await reader.CancelAsync();
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TestBudgets.Contended));
        Array.Fill(buffer, (byte)0x11);
        inner.Release();
        await inner.Finished.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal("11111111", Convert.ToHexString(buffer));
    }

    [Test]
    public async Task ReadAsync_PassesTheBytesThrough_WithoutClosing()
    {
        var closer = Substitute.For<IMxcStreamCloser>();
        using var stream = new MxcCancellableStdoutStream(new MemoryStream("{}\n"u8.ToArray()), closer);
        var buffer = new byte[16];

        var read = await stream.ReadAsync(buffer);

        AssertEx.Equal("{}\n", Encoding.UTF8.GetString(buffer, 0, read));
        closer.DidNotReceive().Close();
    }

    [Test]
    public async Task ReadAsync_AfterTheChildExited_ReturnsEndOfStream()
    {
        var closer = Substitute.For<IMxcStreamCloser>();
        using var stream = new MxcCancellableStdoutStream(new MemoryStream(), closer);

        AssertEx.Equal(0, await stream.ReadAsync(new byte[16]));
        closer.DidNotReceive().Close();
    }
}
