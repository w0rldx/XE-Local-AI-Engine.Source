namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using Microsoft.Mxc.Sdk.V1;

/// <summary>Read-only view of an MXC child's raw stdout whose async reads honour their token.</summary>
/// <remarks>
///     The SDK's stdout Read is synchronous and ignores the token, so an idle child would hold a reader's teardown until it wrote or
///     exited. The read runs on the thread pool; cancellation stops the wait and closes stdout through the SDK's closer, which ends the
///     abandoned native read. A cancelled read is a reader going away, so the bytes it would have returned are not wanted. The worker
///     reads into a private buffer, since an abandoned read could otherwise write into a caller's buffer already returned to its pool.
/// </remarks>
internal sealed class MxcCancellableStdoutStream : Stream
{
    private readonly IMxcStreamCloser? _closer;
    private readonly Stream _inner;
    private Task _pending = Task.CompletedTask;

    public MxcCancellableStdoutStream(Stream inner, IMxcStreamCloser? closer)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _closer = closer;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(RunAsync(buffer, cancellationToken));

    public override void Flush()
    {
        // Read-only: nothing is buffered on this side.
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // An abandoned read may still be inside the inner stream: it is closed only once that read has ended on the closed pipe.
            _ = _pending.ContinueWith(DisposeInner, _inner, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        base.Dispose(disposing);
    }

    private async Task<int> RunAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = new byte[buffer.Length];
        var pending = Task.Run(() => ReadInner(_inner, owned), CancellationToken.None);
        _pending = pending;
        try
        {
            // The inner read returns 0 once the child exited and its pipe drained: end of stream, passed through as such.
            var read = await pending.WaitAsync(cancellationToken);
            owned.AsSpan(0, read).CopyTo(buffer.Span);
            return read;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested
                                          && exception is OperationCanceledException or IOException or ObjectDisposedException or MxcException)
        {
            CloseQuietly();
            throw new OperationCanceledException("The stdout read ended because it was cancelled.", exception, cancellationToken);
        }
    }

    private void CloseQuietly()
    {
        try
        {
            _closer?.Close();
        }
        catch (Exception exception) when (exception is MxcException or ObjectDisposedException)
        {
            // The child already exited or was disposed, so its stdout is closed and the abandoned read has ended on its own.
        }
    }

#pragma warning disable MA0045 // deliberately synchronous: the SDK's async stdout members ignore the token, so these run on the thread pool instead.
    private static int ReadInner(Stream stream, byte[] buffer) => stream.Read(buffer, 0, buffer.Length);

    private static void DisposeInner(Task read, object? inner) => ((Stream)inner!).Dispose();
#pragma warning restore MA0045
}
