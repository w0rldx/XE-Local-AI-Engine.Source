namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Net.Sockets;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;
using XE_Local_AI_Engine.HostAgent.Linux.Security;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HmacAuthenticationInterceptorTests
{
    private const string Secret = "test-secret";
    private const string GetStatusMethodName = "/xe.hostagent.v1.HostAgentControl/GetStatus";
    private const string StreamLogsMethodName = "/xe.hostagent.v1.HostAgentControl/StreamLogs";
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Test]
    public async Task Call_WithValidHmac_Succeeds()
    {
        using var tempDirectory = CreateTempDirectory();
        var socketPath = Path.Combine(tempDirectory.Path, "host-agent.sock");
        await using var app = await StartGrpcServerAsync(socketPath);
        using var channel = CreateChannel(socketPath);
        var client = new HostAgentControl.HostAgentControlClient(channel);

        var request = new Empty();
        var headers = HostAgentHmacMetadata.Create(request, GetStatusMethodName, Secret, new FrozenTimeProvider(FrozenNow), HostAgentHmacOptions.DefaultBucketSeconds);

        var reply = await client.GetStatusAsync(request, headers);

        AssertEx.Equal(HostAgentState.Running, reply.State);
    }

    [Test]
    public async Task Call_WithMissingAuthorization_ThrowsRpcExceptionUnauthenticated()
    {
        using var tempDirectory = CreateTempDirectory();
        var socketPath = Path.Combine(tempDirectory.Path, "host-agent.sock");
        await using var app = await StartGrpcServerAsync(socketPath);
        using var channel = CreateChannel(socketPath);
        var client = new HostAgentControl.HostAgentControlClient(channel);

        var request = new Empty();
        var headers = HostAgentHmacMetadata.Create(request, GetStatusMethodName, Secret, new FrozenTimeProvider(FrozenNow), HostAgentHmacOptions.DefaultBucketSeconds);
        var headersWithoutAuthorization = StripHeader(headers, HostAgentHmacMetadata.AuthorizationHeader);

        var exception = await AssertEx.ThrowsAsync<RpcException>(() => client.GetStatusAsync(request, headersWithoutAuthorization).ResponseAsync);

        AssertEx.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    [Test]
    public async Task Call_WithReplayedRequestId_ThrowsRpcExceptionAlreadyExists()
    {
        using var tempDirectory = CreateTempDirectory();
        var socketPath = Path.Combine(tempDirectory.Path, "host-agent.sock");
        await using var app = await StartGrpcServerAsync(socketPath);
        using var channel = CreateChannel(socketPath);
        var client = new HostAgentControl.HostAgentControlClient(channel);

        var request = new Empty();
        // Reuse the exact same metadata (and therefore the same request id) for both calls.
        var headers = HostAgentHmacMetadata.Create(request, GetStatusMethodName, Secret, new FrozenTimeProvider(FrozenNow), HostAgentHmacOptions.DefaultBucketSeconds);

        var firstReply = await client.GetStatusAsync(request, CloneHeaders(headers));
        AssertEx.Equal(HostAgentState.Running, firstReply.State);

        var exception = await AssertEx.ThrowsAsync<RpcException>(() => client.GetStatusAsync(request, CloneHeaders(headers)).ResponseAsync);

        AssertEx.Equal(StatusCode.AlreadyExists, exception.StatusCode);
    }

    [Test]
    public async Task ServerStreaming_WithInvalidHmac_ThrowsBeforeStreaming()
    {
        using var tempDirectory = CreateTempDirectory();
        var socketPath = Path.Combine(tempDirectory.Path, "host-agent.sock");
        await using var app = await StartGrpcServerAsync(socketPath);
        using var channel = CreateChannel(socketPath);
        var client = new HostAgentControl.HostAgentControlClient(channel);

        var request = new StreamLogsRequest
        {
            ContainerName = "ollama",
            TailLines = 5,
            Follow = false
        };

        // Headers signed with the wrong secret produce a signature mismatch.
        var headers = HostAgentHmacMetadata.Create(request, StreamLogsMethodName, "wrong-secret", new FrozenTimeProvider(FrozenNow), HostAgentHmacOptions.DefaultBucketSeconds);

        var exception = await AssertEx.ThrowsAsync<RpcException>(async () =>
        {
            using var call = client.StreamLogs(request, headers);
            await call.ResponseStream.MoveNext(CancellationToken.None);
        });

        AssertEx.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    private static GrpcChannel CreateChannel(string socketPath)
    {
        var endPoint = new UnixDomainSocketEndPoint(socketPath);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true
        });
    }

    private static async Task<WebApplication> StartGrpcServerAsync(string socketPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenUnixSocket(socketPath, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddSingleton<TimeProvider>(new FrozenTimeProvider(FrozenNow));
        builder.Services.AddSingleton<ReplayWindowCache>();
        builder.Services.AddSingleton<IOptionsMonitor<HostAgentHmacOptions>>(new TestOptionsMonitor<HostAgentHmacOptions>(new HostAgentHmacOptions
        {
            Secret = Secret,
            BucketSeconds = HostAgentHmacOptions.DefaultBucketSeconds,
            MaxRequestIdsPerBucket = HostAgentHmacOptions.DefaultMaxRequestIdsPerBucket
        }));
        builder.Services.AddSingleton<HmacRequestValidator>();
        builder.Services.AddGrpc(options => options.Interceptors.Add<HmacAuthenticationInterceptor>());

        var app = builder.Build();
        app.MapGrpcService<StubHostAgentControlService>();
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    private static Metadata StripHeader(Metadata source, string keyToRemove)
    {
        var headers = new Metadata();
        foreach (var entry in source)
        {
            if (!string.Equals(entry.Key, keyToRemove, StringComparison.OrdinalIgnoreCase))
            {
                headers.Add(entry.Key, entry.Value);
            }
        }

        return headers;
    }

    private static Metadata CloneHeaders(Metadata source)
    {
        var headers = new Metadata();
        foreach (var entry in source)
        {
            headers.Add(entry.Key, entry.Value);
        }

        return headers;
    }

    private static TempDirectory CreateTempDirectory()
    {
        return new TempDirectory(Path.Combine(Path.GetTempPath(), $"xe-host-agent-interceptor-{Guid.NewGuid():N}"));
    }

    private sealed class StubHostAgentControlService : HostAgentControl.HostAgentControlBase
    {
        public override Task<HostAgentStatusReply> GetStatus(Empty request, ServerCallContext context)
        {
            return Task.FromResult(new HostAgentStatusReply
            {
                State = HostAgentState.Running,
                DesiredState = HostAgentDesiredState.Running,
                RuntimeLifecycle = RuntimeLifecycle.Managed,
                BootstrapModelReady = true,
                WebUiUrl = string.Empty,
                ObservedAt = Timestamp.FromDateTimeOffset(FrozenNow)
            });
        }

        public override async Task StreamLogs(StreamLogsRequest request,
            IServerStreamWriter<LogEntryReply> responseStream,
            ServerCallContext context)
        {
            await responseStream.WriteAsync(new LogEntryReply
            {
                ContainerName = request.ContainerName,
                Stream = "stdout",
                Line = "ready",
                ObservedAt = Timestamp.FromDateTimeOffset(FrozenNow)
            }).ConfigureAwait(false);
        }
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FrozenTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    private sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    {
        public TestOptionsMonitor(TOptions value)
        {
            CurrentValue = value;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<TOptions, string?> listener)
        {
            return null;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
