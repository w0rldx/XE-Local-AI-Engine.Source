namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     An idle MXC child's stdout in the SDK's shape: the synchronous Read blocks until the stream is closed under it, then reports end of
///     stream (or <see cref="LateBytes" />), and the inherited async read runs it without ever looking at the token.
/// </summary>
internal sealed class IdleStdout : Stream
{
    private readonly ManualResetEventSlim _closed = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reading => _reading.Task;

    /// <summary>Completes once a released read has written its bytes.</summary>
    public Task Finished => _finished.Task;

    public byte[] LateBytes { get; init; } = [];

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void Release() =>
        _closed.Set();

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        _ = _reading.TrySetResult();
        // A bounded wait so an unfixed run fails the test's own budget first instead of parking a thread forever.
        _ = _closed.Wait(TestBudgets.Contended * 2);
        var read = Math.Min(LateBytes.Length, buffer.Length);
        LateBytes.AsSpan(0, read).CopyTo(buffer);
        _ = _finished.TrySetResult();
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed.Dispose();
        }

        base.Dispose(disposing);
    }
}
