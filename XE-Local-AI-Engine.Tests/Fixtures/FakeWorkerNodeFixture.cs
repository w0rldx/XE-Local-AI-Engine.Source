namespace XE_Local_AI_Engine.Tests.Fixtures;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;

public sealed class FakeWorkerNodeFixture : IAsyncDisposable
{
    private readonly FixtureHubState _hubState = new();

    private IHost? _app;

    public Uri HubBaseUri { get; private set; } = new("https://127.0.0.1");

    public string HubPath { get; } = "/hub/worker";

    public X509Certificate2? ServerCert { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            _app.Dispose();
            _app = null;
        }

        ServerCert?.Dispose();
        ServerCert = null;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            return;
        }

        ServerCert = CreateServerCertificate();

        var builder = Host.CreateDefaultBuilder()
                          .UseEnvironment(Environments.Development)
                          .ConfigureWebHostDefaults(webBuilder =>
                          {
                              webBuilder.UseKestrel(options =>
                              {
                                  options.Listen(IPAddress.Loopback, 0, listenOptions =>
                                  {
                                      listenOptions.UseHttps(ServerCert);
                                      listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                                  });
                              });

                              webBuilder.ConfigureServices(services =>
                              {
                                  services.AddSingleton(_hubState);
                                  services.AddSignalR()
                                          .AddJsonProtocol(options =>
                                          {
                                              options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                                              options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                                              options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                                          });
                              });

                              webBuilder.Configure(app =>
                              {
                                  app.UseRouting();
                                  app.UseEndpoints(endpoints =>
                                  {
                                      endpoints.MapHub<FakeWorkerHub>(HubPath, options =>
                                      {
                                          options.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
                                      });
                                  });
                              });
                          });

        var app = await builder.StartAsync(ct);

        _hubState.HubContext = app.Services.GetRequiredService<IHubContext<FakeWorkerHub>>();

        var addresses = app.Services
                           .GetRequiredService<IServer>()
                           .Features
                           .Get<IServerAddressesFeature>();

        var address = addresses?.Addresses
                               .Select(static value => new Uri(value))
                               .FirstOrDefault(uri => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

        if (address is null)
        {
            await app.StopAsync(ct);
            app.Dispose();
            throw new InvalidOperationException("The fake worker node fixture did not expose an HTTPS address.");
        }

        HubBaseUri = new Uri($"{address.Scheme}://{address.Authority}");
        _app = app;
    }

    public Task SendInvocationAssignedAsync(EncryptedRuntimePackageDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return _hubState.HubContext.Clients.All.SendAsync("InvocationAssigned", dto, ct);
    }

    public Task<EncryptedChunkEnvelopeV1> WaitForFirstChunkAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.ChunkReader, timeout);
    }

    public Task<EncryptedCompletedEnvelopeV1> WaitForCompletedAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.CompletedReader, timeout);
    }

    public Task<(string reason, string nodeKeyIdUsed)> WaitForKeyMismatchAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.KeyMismatchReader, timeout);
    }

    public Task<ClientCapabilitiesPayload> WaitForCapabilitiesAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.CapabilitiesReader, timeout);
    }

    [SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "The plan requires this exact test fixture contract.")]
    public Task FireConnectionDropAsync()
    {
        _hubState.AbortAllConnections();
        return Task.CompletedTask;
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddDnsName("localhost");
        request.CertificateExtensions.Add(sanBuilder.Build());

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [SuppressMessage("Sonar", "S1144:Unused private types or members should be removed", Justification = "SignalR invokes hub methods by name via reflection during integration tests.")]
    [SuppressMessage("Sonar", "S4144:Methods should not have identical implementations", Justification = "SignalR hub endpoints intentionally use simple pass-through handlers in the fixture.")]
    [SuppressMessage("Sonar", "S2325:Methods and properties that don't access instance data should be static", Justification = "SignalR hub methods are instance entrypoints.")]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "SignalR hub methods are instance entrypoints.")]
    private sealed class FakeWorkerHub(FixtureHubState state) : Hub
    {
        private readonly FixtureHubState _state = state;

        public override async Task OnConnectedAsync()
        {
            _state.RegisterConnection(Context);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _state.UnregisterConnection(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        public Task WorkerHello(object payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return Task.CompletedTask;
        }

        public Task InvocationAccepted(object payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return Task.CompletedTask;
        }

        public Task SendEncryptedChunkAsync(EncryptedChunkEnvelopeV1 payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return _state.ChunkWriter.WriteAsync(payload).AsTask();
        }

        public Task SendEncryptedCompletedAsync(EncryptedCompletedEnvelopeV1 payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return _state.CompletedWriter.WriteAsync(payload).AsTask();
        }

        public Task SendEncryptedFailedAsync(EncryptedFailedEnvelopeV1 payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return Task.CompletedTask;
        }

        public Task InvocationKeyMismatch(InvocationKeyMismatchPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return _state.KeyMismatchWriter.WriteAsync((payload.Reason, payload.NodeKeyIdUsed)).AsTask();
        }

        public Task WorkerCapabilitiesReported(ClientCapabilitiesPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return _state.CapabilitiesWriter.WriteAsync(payload).AsTask();
        }

        public Task SendPurgeConversationAsync(Guid conversationId)
        {
            _ = conversationId;
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureHubState
    {
        private readonly Channel<EncryptedChunkEnvelopeV1> _chunks = Channel.CreateUnbounded<EncryptedChunkEnvelopeV1>();
        private readonly Channel<EncryptedCompletedEnvelopeV1> _completed = Channel.CreateUnbounded<EncryptedCompletedEnvelopeV1>();
        private readonly Channel<ClientCapabilitiesPayload> _capabilities = Channel.CreateUnbounded<ClientCapabilitiesPayload>();
        private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new(StringComparer.Ordinal);
        private readonly Channel<(string reason, string nodeKeyIdUsed)> _keyMismatches = Channel.CreateUnbounded<(string reason, string nodeKeyIdUsed)>();

        public IHubContext<FakeWorkerHub> HubContext { get; set; } = null!;

        public ChannelWriter<EncryptedChunkEnvelopeV1> ChunkWriter => _chunks.Writer;

        public ChannelReader<EncryptedChunkEnvelopeV1> ChunkReader => _chunks.Reader;

        public ChannelWriter<EncryptedCompletedEnvelopeV1> CompletedWriter => _completed.Writer;

        public ChannelReader<EncryptedCompletedEnvelopeV1> CompletedReader => _completed.Reader;

        public ChannelWriter<ClientCapabilitiesPayload> CapabilitiesWriter => _capabilities.Writer;

        public ChannelReader<ClientCapabilitiesPayload> CapabilitiesReader => _capabilities.Reader;

        public ChannelWriter<(string reason, string nodeKeyIdUsed)> KeyMismatchWriter => _keyMismatches.Writer;

        public ChannelReader<(string reason, string nodeKeyIdUsed)> KeyMismatchReader => _keyMismatches.Reader;

        public void RegisterConnection(HubCallerContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _connections[context.ConnectionId] = context;
        }

        public void UnregisterConnection(string connectionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
            _connections.TryRemove(connectionId, out _);
        }

        public void AbortAllConnections()
        {
            foreach (var context in _connections.Values)
            {
                context.Abort();
            }
        }

        public static async Task<T> ReadAsync<T>(ChannelReader<T> reader, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);

            try
            {
                return await reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException exception)
            {
                throw new TimeoutException($"Timed out waiting for fixture payload of type '{typeof(T).Name}'.", exception);
            }
        }
    }

    public sealed record ClientCapabilitiesPayload
    {
        public required HardwareCapabilitiesPayload HardwareInfo { get; init; }

        public required SystemCapabilitiesPayload Capabilities { get; init; }
    }

    public sealed record HardwareCapabilitiesPayload
    {
        public int RamMb { get; init; }

        public int VramMb { get; init; }

        public bool CudaAvailable { get; init; }

        public string? GpuName { get; init; }

        public string? CpuClass { get; init; }
    }

    public sealed record SystemCapabilitiesPayload
    {
        public string SystemScoreClass { get; init; } = "Medium";

        public IReadOnlyList<string> InstalledModels { get; init; } = [];

        public IReadOnlyList<string> SupportedCapabilities { get; init; } = [];

        public string? ActiveModel { get; init; }

        public DateTimeOffset? ActiveModelExpiresAt { get; init; }
    }
}
