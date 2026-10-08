namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A child that stopped reading stdin, in the SDK's shape: Write blocks until the pipe is closed under it, then fails as a broken pipe
///     does, and the async write runs it inline without ever looking at the token.
/// </summary>
internal sealed class StalledStdin : Stream
{
    private readonly TaskCompletionSource<byte[]> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _released = new();
    private readonly TaskCompletionSource _writing = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Writing => _writing.Task;

    /// <summary>The bytes the blocked write saw once released: what reached the pipe.</summary>
    public Task<byte[]> Finished => _finished.Task;

    public Exception Failure { get; init; } = new IOException("The pipe is being closed.");

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void Release() =>
        _released.Set();

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _ = _writing.TrySetResult();
        // A bounded wait so an unfixed run fails the test's own budget first instead of parking a thread forever.
        _ = _released.Wait(TestBudgets.Contended * 2);
        _ = _finished.TrySetResult(buffer.ToArray());
        throw Failure;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released.Dispose();
        }

        base.Dispose(disposing);
    }
}
