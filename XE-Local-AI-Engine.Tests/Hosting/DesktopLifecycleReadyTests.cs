namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
public sealed class DesktopLifecycleReadyTests
{
    [Test]
    public void ApplicationStarted_EmitsAndPersistsExactReadyContract_ThenDisposeDeletesIt()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "xe-ready-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        using var output = new StringWriter();
        using var lifetime = new FakeLifetime();
        using var server = new FakeServer();
        try
        {
            using (var lifecycle = new DesktopLifecycle(lifetime,
                       server,
                       NullLogger<DesktopLifecycle>.Instance,
                       dataDirectory,
                       static () => "http://127.0.0.1:41234/",
                       suppressBrowser: true,
                       version: "1.2.3",
                       standardOutput: output))
            {
                lifecycle.Activate();
                lifetime.SignalStarted();

                AssertEx.Equal($"XE_READY=1 XE_VERSION=1.2.3 XE_URL=http://127.0.0.1:41234 XE_MCP_URL=http://127.0.0.1:41234/api/local/v1/mcp/server XE_DATA_DIR={dataDirectory}{Environment.NewLine}",
                    output.ToString());
                var ready = AssertEx.NotNull(DesktopPortStore.ReadReady(dataDirectory));
                AssertEx.Equal("1.2.3", ready.Version);
                AssertEx.Equal(Environment.ProcessId, ready.Pid);
            }

            AssertEx.Null(DesktopPortStore.ReadReady(dataDirectory));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopped = new();
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void SignalStarted() =>
            _started.Cancel();

        public void StopApplication() =>
            _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private sealed class FakeServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public void Dispose() { }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
