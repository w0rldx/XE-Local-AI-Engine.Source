namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using Microsoft.Mxc.Sdk.V1;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="MxcCancellableStdinStream" />: the abandoned write of a cancelled call must neither read the caller's buffer later nor
///     surface a native fault in place of the cancellation.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class MxcCancellableStdinStreamTests
{
    [Test]
    public async Task WriteAsync_WhenCancelled_TheAbandonedWriteSendsTheOriginalBytes_NotTheReusedBuffer()
    {
        // The MCP SDK rents its write buffers: once a write was cancelled the buffer is back in the pool and someone else's.
        using var inner = new StalledStdin();
        var kills = 0;
        using var stream = new MxcCancellableStdinStream(inner, () => Interlocked.Increment(ref kills));
        using var caller = new CancellationTokenSource();
        var bytes = new byte[] { 1, 2, 3 };

        var write = stream.WriteAsync(bytes, caller.Token).AsTask();
        await inner.Writing;
        await caller.CancelAsync();
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => write.WaitAsync(TestBudgets.Contended));
        Array.Fill(bytes, (byte)0x11);
        inner.Release();

        AssertEx.Equal("010203", Convert.ToHexString(await inner.Finished.WaitAsync(TestBudgets.Contended)));
        AssertEx.Equal(1, kills);
    }

    [Test]
    public async Task WriteAsync_WhenANativeWriteFaultRacesTheCancel_TheCallerSeesTheCancellation()
    {
        // MXC throws MxcException, not IOException, for a native write error on a pipe that closed as the caller cancelled.
        using var inner = new StalledStdin { Failure = new MxcException(ErrorCode.BackendError, "the pipe is closed") };
        var kills = 0;
        using var stream = new MxcCancellableStdinStream(inner, () => Interlocked.Increment(ref kills));
        using var caller = new CancellationTokenSource();
        var write = stream.WriteAsync(new byte[] { 1 }, caller.Token).AsTask();
        await inner.Writing;
        // Registered after the wrapper's wait, so it runs first: the fault reaches the wrapper before the cancellation does.
        using var fault = caller.Token.Register(() =>
        {
            inner.Release();
            _ = SpinWait.SpinUntil(() => write.IsCompleted, TestBudgets.Contended);
        });

        await caller.CancelAsync();

        var cancelled = await AssertEx.ThrowsAsync<OperationCanceledException>(() => write.WaitAsync(TestBudgets.Contended));
        _ = AssertEx.NotNull(cancelled.InnerException as MxcException, "the fault must be the one that raced the cancel");
        AssertEx.Equal(1, kills);
    }
}
