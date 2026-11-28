namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>A loopback HTTP server that records the first request body and answers one fixed payload.</summary>
/// <remarks>
///     Same ephemeral-port probe as <c>DeferredLlamaServerEmbeddingGeneratorFailureTests.StubServer</c>, which cannot be
///     reused directly because it captures nothing.
/// </remarks>
internal sealed class CapturingServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _captured = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CapturingServer(HttpListener listener, Uri baseAddress)
    {
        _listener = listener;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    // The request completes before the assertion runs, so the body is already there; the task is only how the
    // listener thread hands it over without a lock.
    public string? CapturedBody => _captured.Task.IsCompletedSuccessfully ? _captured.Task.Result : null;

    public static CapturingServer Start(string body, string contentType)
    {
        // HttpListener has no "bind port 0" mode, so ask the OS for a free one through a throwaway TcpListener.
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, port: 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = new CapturingServer(listener, new Uri($"http://127.0.0.1:{port}/v1"));
        server.Serve(body, contentType);
        return server;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        _cts.Dispose();
    }

    private void Serve(string body, string contentType)
    {
        _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (HttpListenerException)
                    {
                        return; // Disposed.
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        _captured.TrySetResult(await reader.ReadToEndAsync(_cts.Token));
                    }

                    var payload = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = contentType;
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload, _cts.Token);
                    context.Response.Close();
                }
            },
            _cts.Token);
    }
}
