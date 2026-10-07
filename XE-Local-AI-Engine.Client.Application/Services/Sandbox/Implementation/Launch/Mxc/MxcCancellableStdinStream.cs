namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using Microsoft.Mxc.Sdk.V1;

/// <summary>Write-only view of an MXC child's stdin whose async writes honour their token; disposing it sends EOF.</summary>
/// <remarks>
///     The SDK's stdin Write is synchronous and ignores the token, so a child that stops reading would outlast every cancel. The write
///     runs on the thread pool; cancellation stops the wait and kills the child, whose closed pipe ends the abandoned write. A stalled
///     child is dead to its caller anyway, and a half-written frame could not be resumed. The worker writes a private copy of the
///     bytes, since an abandoned write could otherwise read a caller's buffer already returned to its pool.
/// </remarks>
internal sealed class MxcCancellableStdinStream : Stream
{
    private readonly Stream _inner;
    private readonly Action _kill;
    private Task _pending = Task.CompletedTask;

    public MxcCancellableStdinStream(Stream inner, Action kill)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _kill = kill ?? throw new ArgumentNullException(nameof(kill));
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var owned = buffer.ToArray();
        return new(RunAsync(stream => stream.Write(owned), cancellationToken));
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => RunAsync(FlushInner, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // An abandoned write may still be inside the inner stream: it is closed only once that write has ended on the killed pipe.
            _ = _pending.ContinueWith(DisposeInner, _inner, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        base.Dispose(disposing);
    }

    private async Task RunAsync(Action<Stream> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = Task.Run(() => operation(_inner), CancellationToken.None);
        _pending = pending;
        try
        {
            await pending.WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested
                                          && exception is OperationCanceledException or IOException or ObjectDisposedException or MxcException)
        {
            // Killed before the cancellation surfaces, so the caller never sees a cancelled write while the child still runs.
            _kill();
            throw new OperationCanceledException("The stdin write ended because it was cancelled.", exception, cancellationToken);
        }
    }

#pragma warning disable MA0045 // deliberately synchronous: the SDK's async stdin members ignore the token, so these run on the thread pool instead.
    private static void FlushInner(Stream stream) => stream.Flush();

    private static void DisposeInner(Task write, object? inner) => ((Stream)inner!).Dispose();
#pragma warning restore MA0045
}
