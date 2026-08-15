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
using Microsoft.AspNetCore.Connections.Features;
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

/// <summary>
///     A real, self-hosted stand-in for the <em>platform</em> side of the WorkerHub connection: a loopback Kestrel host
///     with a self-signed certificate serving a SignalR hub at <see cref="HubPath" />, so the node's real
///     <c>IWorkerHubConnection</c> negotiates, connects, and speaks the live protocol against it.
/// </summary>
/// <remarks>
///     <para>
///         Use this fixture when the behaviour under test <em>is</em> the connection — negotiation, reconnect after a
///         transport drop, heartbeat cadence, capability reporting, or an envelope's trip across a real SignalR wire.
///         Every <c>WaitFor…</c> method reads from an unbounded channel the hub writes to, so a test asserts on what the
///         node actually sent rather than on a mock's recorded call.
///     </para>
///     <para>
///         Prefer <c>RecordingHubMessageSender</c> (in <c>XE-Local-AI-Engine.Client.Testing</c>) when the behaviour under
///         test is the <em>outbound contract</em> — which calls the node makes, in what order, with what payload. That
///         decorator records against the in-process <c>IHubMessageSender</c> with no host, no TLS, and no network, so it
///         is far cheaper and cannot flake on a timeout. Reach for this fixture only when a real transport is the point.
///     </para>
/// </remarks>
public sealed class FakeWorkerNodeFixture : IAsyncDisposable
{
    private readonly FixtureHubState _hubState = new();

    private IHost? _app;

    /// <summary>The loopback base address the hub listens on. Only meaningful after <see cref="StartAsync" /> — before that it is a placeholder.</summary>
    public Uri HubBaseUri { get; private set; } = new("https://127.0.0.1");

    /// <summary>The path the fake WorkerHub is mapped at; combine with <see cref="HubBaseUri" /> to point a client at it.</summary>
    public string HubPath { get; } = "/hub/worker";

    /// <summary>The self-signed certificate this host serves TLS with, so a test can pin or trust it. Null until <see cref="StartAsync" />.</summary>
    public X509Certificate2? ServerCert { get; private set; }

    /// <summary>How many capability reports the hub has received so far — for asserting the reporter fired once, not twice.</summary>
    public int CapabilitiesReportCount => _hubState.CapabilitiesReportCount;

    /// <summary>Stops the host and disposes the server certificate. Safe to call when <see cref="StartAsync" /> was never called.</summary>
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

    /// <summary>
    ///     Starts the loopback HTTPS host and publishes its address on <see cref="HubBaseUri" />. Idempotent: a second
    ///     call on a started fixture returns immediately.
    /// </summary>
    /// <exception cref="InvalidOperationException">The host started but exposed no HTTPS address.</exception>
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
                                  options.Listen(IPAddress.Loopback, port: 0, listenOptions =>
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

    /// <summary>Pushes an <c>InvocationAssigned</c> message to every connected client — the platform-initiated half of the protocol.</summary>
    public Task SendInvocationAssignedAsync(EncryptedRuntimePackageDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return _hubState.HubContext.Clients.All.SendAsync("InvocationAssigned", dto, ct);
    }

    /// <summary>Reads the next streamed chunk envelope the node sent, or throws <see cref="TimeoutException" /> when none arrives in time.</summary>
    public Task<EncryptedChunkEnvelopeV1> WaitForFirstChunkAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.ChunkReader, timeout);
    }

    /// <summary>Reads the next completion envelope the node sent, or throws <see cref="TimeoutException" /> when none arrives in time.</summary>
    public Task<EncryptedCompletedEnvelopeV1> WaitForCompletedAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.CompletedReader, timeout);
    }

    /// <summary>Reads the next key-mismatch report (reason plus the node key id that was used), or throws <see cref="TimeoutException" />.</summary>
    public Task<(string reason, string nodeKeyIdUsed)> WaitForKeyMismatchAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.KeyMismatchReader, timeout);
    }

    /// <summary>Reads the next capability report the node pushed, or throws <see cref="TimeoutException" /> when none arrives in time.</summary>
    public Task<ClientCapabilitiesPayload> WaitForCapabilitiesAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.CapabilitiesReader, timeout);
    }

    /// <summary>Reads the next heartbeat the node sent, or throws <see cref="TimeoutException" /> when none arrives in time.</summary>
    public Task<HeartbeatPayload> WaitForHeartbeatAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.HeartbeatReader, timeout);
    }

    /// <summary>Reads the client node id from the next <c>WorkerHello</c> handshake, or throws <see cref="TimeoutException" />.</summary>
    public Task<Guid> WaitForWorkerHelloAsync(TimeSpan timeout)
    {
        return FixtureHubState.ReadAsync(_hubState.WorkerHelloReader, timeout);
    }

    /// <summary>
    ///     Aborts every hub connection <em>gracefully</em> (SignalR close frame, <c>allowReconnect:false</c>), so the client
    ///     tears down and does NOT auto-reconnect. Use <see cref="FireTransportLevelConnectionDropAsync" /> to test reconnect.
    /// </summary>
    [SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "Test fixture trigger method; an event would not fit the deterministic drive-the-hub contract callers rely on.")]
    public Task FireConnectionDropAsync()
    {
        _hubState.AbortAllConnections();
        return Task.CompletedTask;
    }

    // HubCallerContext.Abort() sends a graceful SignalR close message with allowReconnect:false, so the
    // client tears the connection down and intentionally does NOT auto-reconnect. To exercise the
    // reconnect path we must drop the underlying transport abruptly (no close frame) so the client's
    // receive loop observes a transport error and WithAutomaticReconnect engages. This aborts the
    // connection at the transport layer via IConnectionLifetimeFeature, mimicking a real network loss.
    /// <summary>
    ///     Kills every connection at the transport layer with no close frame, mimicking real network loss so the client's
    ///     <c>WithAutomaticReconnect</c> engages. This is the drop to use for reconnect tests.
    /// </summary>
    [SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "Matches the existing FireConnectionDropAsync fixture contract.")]
    public Task FireTransportLevelConnectionDropAsync()
    {
        _hubState.AbortAllTransports();
        return Task.CompletedTask;
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddDnsName("localhost");
        request.CertificateExtensions.Add(sanBuilder.Build());

        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // The PKCS#12 round-trip is mandatory on Windows and must not be "simplified" back to returning `ephemeral`.
        // CreateSelfSigned hands back a certificate whose private key lives only in this process's memory. OpenSSL —
        // so, Linux — serves TLS from that happily. Windows serves TLS through SChannel, which can only use a private
        // key it can reach through a CNG/CAPI key container, and an ephemeral key is in none. Kestrel accepts the
        // certificate at UseHttps and then aborts every handshake, which reaches the client as the entirely unhelpful
        // "The SSL connection could not be established ... Received an unexpected EOF or 0 bytes from the transport
        // stream" — with nothing on the server side naming the cause.
        //
        // Exporting to PFX and loading it back is what gives the key a container Windows can open. EphemeralKeySet is
        // deliberately NOT passed: that flag re-creates exactly the problem this works around.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
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

        public Task WorkerHello(JsonElement payload)
        {
            var clientNodeId = payload.GetProperty("clientNodeId").GetGuid();
            return _state.WorkerHelloWriter.WriteAsync(clientNodeId).AsTask();
        }

        public Task Heartbeat(JsonElement payload)
        {
            var clientNodeId = payload.GetProperty("clientNodeId").GetGuid();
            var timestamp = payload.GetProperty("timestamp").GetDateTimeOffset();
            return _state.HeartbeatWriter.WriteAsync(new HeartbeatPayload
            {
                ClientNodeId = clientNodeId,
                Timestamp = timestamp
            }).AsTask();
        }

        public Task WorkerKeyRegistered(JsonElement payload)
        {
            _ = payload;
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
            _state.IncrementCapabilitiesReportCount();
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
        private readonly Channel<ClientCapabilitiesPayload> _capabilities = Channel.CreateUnbounded<ClientCapabilitiesPayload>();
        private readonly Channel<EncryptedChunkEnvelopeV1> _chunks = Channel.CreateUnbounded<EncryptedChunkEnvelopeV1>();
        private readonly Channel<EncryptedCompletedEnvelopeV1> _completed = Channel.CreateUnbounded<EncryptedCompletedEnvelopeV1>();
        private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new(StringComparer.Ordinal);
        private readonly Channel<HeartbeatPayload> _heartbeats = Channel.CreateUnbounded<HeartbeatPayload>();
        private readonly Channel<(string reason, string nodeKeyIdUsed)> _keyMismatches = Channel.CreateUnbounded<(string reason, string nodeKeyIdUsed)>();
        private readonly Channel<Guid> _workerHellos = Channel.CreateUnbounded<Guid>();
        private int _capabilitiesReportCount;

        public IHubContext<FakeWorkerHub> HubContext { get; set; } = null!;

        public int CapabilitiesReportCount => Volatile.Read(ref _capabilitiesReportCount);

        public ChannelWriter<HeartbeatPayload> HeartbeatWriter => _heartbeats.Writer;

        public ChannelReader<HeartbeatPayload> HeartbeatReader => _heartbeats.Reader;

        public ChannelWriter<Guid> WorkerHelloWriter => _workerHellos.Writer;

        public ChannelReader<Guid> WorkerHelloReader => _workerHellos.Reader;

        public ChannelWriter<EncryptedChunkEnvelopeV1> ChunkWriter => _chunks.Writer;

        public ChannelReader<EncryptedChunkEnvelopeV1> ChunkReader => _chunks.Reader;

        public ChannelWriter<EncryptedCompletedEnvelopeV1> CompletedWriter => _completed.Writer;

        public ChannelReader<EncryptedCompletedEnvelopeV1> CompletedReader => _completed.Reader;

        public ChannelWriter<ClientCapabilitiesPayload> CapabilitiesWriter => _capabilities.Writer;

        public ChannelReader<ClientCapabilitiesPayload> CapabilitiesReader => _capabilities.Reader;

        public ChannelWriter<(string reason, string nodeKeyIdUsed)> KeyMismatchWriter => _keyMismatches.Writer;

        public ChannelReader<(string reason, string nodeKeyIdUsed)> KeyMismatchReader => _keyMismatches.Reader;

        public void IncrementCapabilitiesReportCount()
        {
            Interlocked.Increment(ref _capabilitiesReportCount);
        }

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

        public void AbortAllTransports()
        {
            foreach (var context in _connections.Values)
            {
                var lifetime = context.Features.Get<IConnectionLifetimeFeature>()
                               ?? throw new InvalidOperationException("IConnectionLifetimeFeature is unavailable; a transport-level drop cannot be simulated. "
                                                                      + "Reconnect tests require WebSockets so this feature is present.");
                lifetime.Abort();
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

    /// <summary>Wire shape of the <c>WorkerCapabilitiesReported</c> payload as this fixture deserializes it.</summary>
    public sealed record ClientCapabilitiesPayload
    {
        /// <summary>The reported hardware block (RAM, VRAM, CUDA, GPU/CPU identity).</summary>
        public required HardwareCapabilitiesPayload HardwareInfo { get; init; }

        /// <summary>The reported software/runtime block (score class, provider reachability, installed models).</summary>
        public required SystemCapabilitiesPayload Capabilities { get; init; }
    }

    /// <summary>Hardware half of a capability report.</summary>
    public sealed record HardwareCapabilitiesPayload
    {
        /// <summary>Total system RAM in megabytes, as the node measured it.</summary>
        public int RamMb { get; init; }

        /// <summary>Total GPU VRAM in megabytes; zero when no GPU was detected.</summary>
        public int VramMb { get; init; }

        /// <summary>Whether the node found a usable CUDA runtime.</summary>
        public bool CudaAvailable { get; init; }

        /// <summary>Reported GPU model name; null when none was detected.</summary>
        public string? GpuName { get; init; }

        /// <summary>Coarse CPU capability class the node assigned itself; null when unclassified.</summary>
        public string? CpuClass { get; init; }
    }

    /// <summary>Software/runtime half of a capability report. Defaults here mirror a plausible report so a test need only set the fields it asserts on.</summary>
    public sealed record SystemCapabilitiesPayload
    {
        /// <summary>Capability-payload schema version the node emitted.</summary>
        public int SchemaVersion { get; init; } = 2;

        /// <summary>Overall box class (Low/Medium/High) derived from the hardware block.</summary>
        public string SystemScoreClass { get; init; } = "Medium";

        /// <summary>Whether the gated Ollama secondary provider answered; null when it was not probed.</summary>
        public bool? OllamaReachable { get; init; }

        /// <summary>Version string Ollama reported, when reachable.</summary>
        public string? OllamaVersion { get; init; }

        /// <summary>How the runtime is managed on this node (app-managed vs external).</summary>
        public string ManagementMode { get; init; } = "unknown";

        /// <summary>When the node last produced a capability report; null on the first one.</summary>
        public DateTimeOffset? LastCapabilityReportAt { get; init; }

        /// <summary>Human-readable diagnostic notes the probe collected; empty on a clean box.</summary>
        public IReadOnlyList<string> Diagnostics { get; init; } = [];

        /// <summary>Names of the models installed locally.</summary>
        public IReadOnlyList<string> InstalledModels { get; init; } = [];

        /// <summary>Per-model metadata for <see cref="InstalledModels" /> (digest, context window).</summary>
        public IReadOnlyList<ModelMetadataPayload> InstalledModelMetadata { get; init; } = [];

        /// <summary>Capability tokens the node advertises (tools, vision, thinking, …).</summary>
        public IReadOnlyList<string> SupportedCapabilities { get; init; } = [];

        /// <summary>Model currently loaded in the runtime; null when nothing is warm.</summary>
        public string? ActiveModel { get; init; }

        /// <summary>When the warm model is due to be evicted; null when nothing is warm or it never expires.</summary>
        public DateTimeOffset? ActiveModelExpiresAt { get; init; }
    }

    /// <summary>Per-model metadata carried alongside the installed-model list.</summary>
    public sealed record ModelMetadataPayload
    {
        /// <summary>Model name as the runtime knows it.</summary>
        public required string Name { get; init; }

        /// <summary>Content digest of the model weights, when the runtime exposes one.</summary>
        public string? Digest { get; init; }

        /// <summary>Maximum context window in tokens, when known.</summary>
        public int? MaxContextTokens { get; init; }
    }
}
