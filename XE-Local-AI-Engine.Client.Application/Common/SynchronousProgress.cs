namespace XE_Local_AI_Engine.Client.Common;

/// <summary>
///     An <see cref="IProgress{T}" /> that invokes its handler inline on the reporting thread: synchronous reports from
///     one producer are applied in order before the producer continues. Asynchronous work a handler starts itself stays
///     unordered.
/// </summary>
/// <remarks>
///     <see cref="Progress{T}" /> is unusable on the background paths: with no <see cref="SynchronizationContext" /> it
///     posts each report to the thread pool, so reports arrive out of order and a late one can overwrite a phase the
///     producer recorded after it. A handler must not block, since it runs on the producer's thread, and an exception it
///     throws surfaces to the producer rather than the thread pool.
/// </remarks>
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SynchronousProgress(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public void Report(T value)
    {
        _handler(value);
    }
}
