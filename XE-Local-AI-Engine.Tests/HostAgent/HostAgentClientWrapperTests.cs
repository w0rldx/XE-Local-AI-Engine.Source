namespace XE_Local_AI_Engine.Tests.HostAgent;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;
using XE_Local_AI_Engine.HostAgent.Linux.Security;
using XE_Local_AI_Engine.Tests.Testing;
using ContainerDesiredState = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.ContainerDesiredState;
using ContainerHealth = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.ContainerHealth;
using ContainerHealthDto = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts.ContainerHealth;
using HostAgentDesiredState = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.HostAgentDesiredState;
using HostAgentDesiredStateDto = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts.HostAgentDesiredState;
using HostAgentState = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.HostAgentState;
using HostAgentStateDto = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts.HostAgentState;
using RuntimeLifecycle = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.RuntimeLifecycle;
using RuntimeLifecycleDto = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts.RuntimeLifecycle;

public sealed class HostAgentClientWrapperTests
{
    private const string Secret = "test-secret";
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Test]
    public async Task GetStatusAsync_ConnectsOverUnixSocketAndPopulatesHmacHeaders()
    {
        using var tempDirectory = CreateTempDirectory();
        var socketPath = Path.Combine(tempDirectory.Path, "host-agent.sock");
        var capturedCalls = new CapturedHostAgentCalls();
        await using var app = await StartGrpcServerAsync(socketPath, capturedCalls);
        using var client = CreateClient(socketPath);

        var status = await client.GetStatusAsync(CancellationToken.None);

        AssertEx.Equal(HostAgentStateDto.Running, status.State);
        AssertEx.Equal(HostAgentDesiredStateDto.Running, status.DesiredState);
        AssertEx.Equal(RuntimeLifecycleDto.Managed, status.RuntimeLifecycle);
        AssertEx.True(status.BootstrapModelReady);
        AssertEx.Equal("http://127.0.0.1:8080", status.WebUiUrl);
        AssertEx.ContainsSingle(status.Components, component => component.Name == "ollama" && component.Health == ContainerHealthDto.Healthy);
        ValidateSingleCapturedCall(capturedCalls, "/xe.hostagent.v1.HostAgentControl/GetStatus");
    }

    [Test]
    public async Task StreamLogsAsync_ConnectsOverUnixSocketAndPopulatesHmacHeaders()
    {
        using var tempDirectory = CreateTempDirectory();
        var socketPath = Path.Combine(tempDirectory.Path, "host-agent.sock");
        var capturedCalls = new CapturedHostAgentCalls();
        await using var app = await StartGrpcServerAsync(socketPath, capturedCalls);
        using var client = CreateClient(socketPath);
        var lines = new List<HostAgentLogLineDto>();

        await foreach (var line in client.StreamLogsAsync("ollama", 25, true, CancellationToken.None))
        {
            lines.Add(line);
        }

        AssertEx.ContainsSingle(lines, line => line.ContainerName == "ollama" && line.Line == "ready");
        ValidateSingleCapturedCall(capturedCalls, "/xe.hostagent.v1.HostAgentControl/StreamLogs");
    }

    private static GrpcHostAgentClient CreateClient(string socketPath)
    {
        return new GrpcHostAgentClient(new HostAgentClientOptions
        {
            SocketPath = socketPath,
            Secret = Secret,
            BucketSeconds = HostAgentHmacOptions.DefaultBucketSeconds
        }, new FrozenTimeProvider(FrozenNow));
    }

    private static async Task<WebApplication> StartGrpcServerAsync(string socketPath, CapturedHostAgentCalls capturedCalls)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenUnixSocket(socketPath, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(capturedCalls);

        var app = builder.Build();
        app.MapGrpcService<CapturingHostAgentControlService>();
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    private static void ValidateSingleCapturedCall(CapturedHostAgentCalls capturedCalls, string expectedMethodName)
    {
        AssertEx.Equal(1, capturedCalls.Calls.Count);
        var call = capturedCalls.Calls[0];
        AssertEx.Equal(expectedMethodName, call.MethodName);
        AssertEx.NotNullOrEmpty(call.Headers.GetValue(HostAgentHmacMetadata.RequestIdHeader));
        AssertEx.NotNullOrEmpty(call.Headers.GetValue(HostAgentHmacMetadata.BodySha256Header));
        AssertEx.NotNullOrEmpty(call.Headers.GetValue(HostAgentHmacMetadata.AuthorizationHeader));

        var validator = CreateValidator();
        var result = validator.Validate(call.Request, call.Headers, call.MethodName);
        AssertEx.True(result.Succeeded, result.Detail);
    }

    private static HmacRequestValidator CreateValidator()
    {
        var options = new TestOptionsMonitor<HostAgentHmacOptions>(new HostAgentHmacOptions
        {
            Secret = Secret,
            BucketSeconds = HostAgentHmacOptions.DefaultBucketSeconds,
            MaxRequestIdsPerBucket = HostAgentHmacOptions.DefaultMaxRequestIdsPerBucket
        });

        return new HmacRequestValidator(options, new ReplayWindowCache(), new FrozenTimeProvider(FrozenNow));
    }

    private static TempDirectory CreateTempDirectory()
    {
        return new TempDirectory(Path.Combine(Path.GetTempPath(), $"xe-host-agent-client-{Guid.NewGuid():N}"));
    }

    public sealed class CapturingHostAgentControlService : HostAgentControl.HostAgentControlBase
    {
        private readonly CapturedHostAgentCalls _capturedCalls;

        public CapturingHostAgentControlService(CapturedHostAgentCalls capturedCalls)
        {
            _capturedCalls = capturedCalls;
        }

        public override Task<HostAgentStatusReply> GetStatus(Empty request, ServerCallContext context)
        {
            _capturedCalls.Add(request, context);
            var reply = new HostAgentStatusReply
            {
                State = HostAgentState.Running,
                DesiredState = HostAgentDesiredState.Running,
                RuntimeLifecycle = RuntimeLifecycle.Managed,
                BootstrapModelReady = true,
                WebUiUrl = "http://127.0.0.1:8080",
                ObservedAt = Timestamp.FromDateTimeOffset(FrozenNow),
                Diagnostics =
                {
                    "ok"
                }
            };
            reply.Components.Add(new RuntimeComponentStatusReply
            {
                Name = "ollama",
                DesiredState = ContainerDesiredState.Running,
                Health = ContainerHealth.Healthy,
                ImageReference = "ollama/ollama:0.11.10@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                DigestVerified = true,
                ObservedAt = Timestamp.FromDateTimeOffset(FrozenNow),
                Diagnostics =
                {
                    "healthy"
                }
            });

            return Task.FromResult(reply);
        }

        public override async Task StreamLogs(StreamLogsRequest request,
            IServerStreamWriter<LogEntryReply> responseStream,
            ServerCallContext context)
        {
            _capturedCalls.Add(request, context);
            await responseStream.WriteAsync(new LogEntryReply
            {
                ContainerName = request.ContainerName,
                Stream = "stdout",
                Line = "ready",
                ObservedAt = Timestamp.FromDateTimeOffset(FrozenNow)
            }).ConfigureAwait(false);
        }
    }

    public sealed class CapturedHostAgentCalls
    {
        public List<CapturedHostAgentCall> Calls { get; } = [];

        public void Add(IMessage request, ServerCallContext context)
        {
            var headers = new Metadata();
            foreach (var entry in context.RequestHeaders)
            {
                headers.Add(entry.Key, entry.Value);
            }

            Calls.Add(new CapturedHostAgentCall(request, headers, context.Method));
        }
    }

    public sealed record CapturedHostAgentCall(IMessage Request, Metadata Headers, string MethodName);

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
