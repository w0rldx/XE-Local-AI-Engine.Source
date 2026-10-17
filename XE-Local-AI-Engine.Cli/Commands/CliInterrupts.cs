namespace XE_Local_AI_Engine.Cli.Commands;

/// <summary>Turns Ctrl-C into a cancellation token; a command that handled one starts a fresh token with <see cref="Reset" />.</summary>
internal sealed class CliInterrupts : IDisposable
{
    private CancellationTokenSource _current = new();

    public CancellationToken Token => Volatile.Read(ref _current).Token;

    /// <summary>Called from the synchronous <c>Console.CancelKeyPress</c> handler.</summary>
    public void Raise()
    {
#pragma warning disable MA0045 // Console.CancelKeyPress is a synchronous event handler.
        Volatile.Read(ref _current).Cancel();
#pragma warning restore MA0045
    }

    /// <summary>Arms a fresh token after an interrupt was handled, for example when the chat returns to its prompt.</summary>
    public CancellationToken Reset()
    {
        if (Volatile.Read(ref _current).IsCancellationRequested)
        {
            // The spent source is not disposed: a registration made on its token may still be unwinding.
            Interlocked.Exchange(ref _current, new CancellationTokenSource());
        }

        return Token;
    }

    public void Dispose() =>
        Volatile.Read(ref _current).Dispose();
}
