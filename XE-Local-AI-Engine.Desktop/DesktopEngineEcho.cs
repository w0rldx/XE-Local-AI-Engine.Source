namespace XE_Local_AI_Engine.Desktop;

using System.Threading.Channels;

/// <summary>
///     Debug mode's copy of an owned engine's console output to this shell's own console.
/// </summary>
/// <remarks>
///     A Windows console with a text selection, or a paused pipe, blocks writes. Writing from the drain loops would then
///     fill the engine's stdout pipe and stall its synchronous console sink, so the drains only enqueue into a bounded
///     channel that drops the oldest entries, and one writer task pays for a slow console.
/// </remarks>
internal sealed class DesktopEngineEcho : IAsyncDisposable
{
    internal const int Capacity = 1024;

    private readonly Channel<(bool Error, string Text)> _pending = Channel.CreateBounded<(bool Error, string Text)>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    private readonly Task _writer;

    internal DesktopEngineEcho(TextWriter output, TextWriter error) =>
        _writer = Task.Run(() => WriteAsync(output, error), CancellationToken.None);

    /// <summary>Queues one standard-output line; never blocks.</summary>
    internal void Output(string line) =>
        _pending.Writer.TryWrite((false, line));

    /// <summary>Queues a raw standard-error chunk; never blocks.</summary>
    internal void Error(string text) =>
        _pending.Writer.TryWrite((true, text));

    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // A console that stays blocked keeps its writer; shutdown does not wait for it.
        }
    }

    private async Task WriteAsync(TextWriter output, TextWriter error)
    {
        await foreach (var (isError, text) in _pending.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                if (isError)
                {
                    await error.WriteAsync(text);
                }
                else
                {
                    await output.WriteLineAsync(text);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // A closed terminal or pipe ends the echo, never the engine session.
            }
        }
    }
}
