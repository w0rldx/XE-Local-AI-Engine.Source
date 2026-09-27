namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.Net.Sockets;

/// <summary>
///     The one "the llama-server child is gone" match, shared by this provider's self-healing clients and the host's
///     raw-model proxy, so the two never disagree about what a force-eject looks like on the wire.
/// </summary>
public static class LlamaServerConnectionFailure
{
    /// <summary>
    ///     True when the exception chain says the target llama-server is unreachable (the process is gone) rather
    ///     than reporting a model or runtime error.
    /// </summary>
    /// <remarks>
    ///     Walks the FULL chain, including the AggregateException fan-out from the OpenAI SDK retry policy:
    ///     ClientResultException, HttpRequestException, a refused SocketException.
    /// </remarks>
    public static bool IsServerGone(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var queue = new Queue<Exception>();
        queue.Enqueue(exception);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            switch (current)
            {
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.HostUnreachable or SocketError.TimedOut }:
                    return true;
                case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }:
                    return true;
                // A process killed MID-RESPONSE does not surface as a connect-time failure: the open body stream terminates as HttpIOException(ResponseEnded),
                // live-observed during a force-eject. Without this arm the ejected-lease translation never fires and the user sees a generic provider failure.
                case HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError }:
                    return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var nested in aggregate.InnerExceptions)
                {
                    queue.Enqueue(nested);
                }
            }
            else if (current.InnerException is not null)
            {
                queue.Enqueue(current.InnerException);
            }
        }

        return false;
    }
}
