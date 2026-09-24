namespace XE_Local_AI_Engine.Tests.Containers.Bridge;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The bridge branch over a REAL Kestrel socket, which is the only way to exercise it: the in-memory TestServer
///     presents no peer address, so the peer guard cannot be reached through it, and the branch predicate is the
///     arrival port, which an in-memory transport does not have either.
///     <para>
///         The forwarder is the real <c>LocalModelProxyForwarder</c> over substituted collaborators rather than a
///         stand-in, because <c>ContainerBridgePipeline</c> resolves that concrete type — a stand-in would be testing
///         a wiring the product does not have.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ContainerBridgePipelineTests
{
    private const string ModelName = "bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M";

    [Test]
    public async Task Bridge_WithoutAToken_Answers401AndNeverReachesTheForwarder()
    {
        await using var bridge = await BridgeHost.StartAsync();

        using var response = await bridge.GetAsync(ContainerBridgePipeline.ModelsPath, token: null);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertEx.Equal("Bearer", AssertEx.NotNull(response.Headers.WwwAuthenticate.FirstOrDefault()).Scheme);
        await bridge.Models.DidNotReceiveWithAnyArgs().ListInstalledModelsAsync(default);
    }

    [Test]
    public async Task Bridge_WithAnUnknownToken_Answers401()
    {
        await using var bridge = await BridgeHost.StartAsync();

        using var response = await bridge.GetAsync(ContainerBridgePipeline.ModelsPath, ContainerBridgeToken.Mint(Guid.NewGuid()));

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await bridge.Models.DidNotReceiveWithAnyArgs().ListInstalledModelsAsync(default);
    }

    /// <summary>
    ///     The end-to-end statement of what the bridge is for: a caller holding its instance's token reaches the
    ///     node's own model surface, through the peer guard, the token gate and the forwarder the loopback proxy uses.
    /// </summary>
    [Test]
    public async Task Bridge_WithTheInstancesToken_ForwardsToTheLocalModelSurface()
    {
        await using var bridge = await BridgeHost.StartAsync();

        using var response = await bridge.GetAsync(ContainerBridgePipeline.ModelsPath, bridge.ValidToken);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, ModelName);
        await bridge.Models.Received(1).ListInstalledModelsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Bridge_OnAnUnknownPath_Answers404InTheOpenAiErrorEnvelope()
    {
        await using var bridge = await BridgeHost.StartAsync();

        using var response = await bridge.GetAsync("/llm/v1/nothing-here", bridge.ValidToken);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Equal(ContainerBridgePipeline.NotFoundBody, body);
    }

    /// <summary>
    ///     Nothing else the host serves is reachable on the bridge port. These are the paths a node answers on its
    ///     loopback listener; on this one they are all the same nothing.
    /// </summary>
    [Test]
    [Arguments("/")]
    [Arguments("/index.html")]
    [Arguments("/api/local/v1/external-apps")]
    [Arguments("/health/ready")]
    public async Task Bridge_ServesNothingTheLoopbackListenerServes(string path)
    {
        await using var bridge = await BridgeHost.StartAsync();

        using var response = await bridge.GetAsync(path, bridge.ValidToken);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"'{path}' belongs to the loopback listener and must not exist on the bridge.");
    }

    [Test]
    public async Task Bridge_OnAKnownPathWithTheWrongMethod_Answers405AndNamesTheMethodItTakes()
    {
        await using var bridge = await BridgeHost.StartAsync();

        using var response = await bridge.GetAsync(ContainerBridgePipeline.ChatCompletionsPath, bridge.ValidToken);

        AssertEx.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        AssertEx.Contains(response.Content.Headers.Allow, "POST");
    }

    /// <summary>
    ///     A real Kestrel listener running exactly what <see cref="ContainerBridgePipeline.Map" /> builds: the peer
    ///     guard, the token gate and the three routes, with the real forwarder over substituted collaborators.
    /// </summary>
    private sealed class BridgeHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly ContainerBridgeAddressWatcher _watcher;

        private BridgeHost(WebApplication app, HttpClient client, ContainerBridgeAddressWatcher watcher, IGgufModelStore models, string validToken)
        {
            _app = app;
            _client = client;
            _watcher = watcher;
            Models = models;
            ValidToken = validToken;
        }

        public string ValidToken { get; }

        /// <summary>The forwarder's own model source, so "never reached the forwarder" is an assertion rather than an inference from a status code.</summary>
        public IGgufModelStore Models { get; }

        public static async Task<BridgeHost> StartAsync()
        {
            var instanceId = Guid.NewGuid();
            var validToken = ContainerBridgeToken.Mint(instanceId);
            var port = ReserveLoopbackPort();

            var builder = WebApplication.CreateSlimBuilder();
#pragma warning disable S5332 // The bridge is plain HTTP by design; this binds loopback in-process for one test.
            builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
#pragma warning restore S5332

            // Loopback IS this fixture's bridge address: the listener really is bound there, so the branch predicate
            // (which matches the whole local endpoint, not the port alone) recognises these connections.
            var endpoint = new ResolvedContainerBridgeEndpoint(IPAddress.Loopback, port, $"127.0.0.1:{port}");

            // The production call. Loopback is already in the shipped AllowedHosts, so this changes nothing here —
            // it is in place so this fixture and the real-daemon one build the host the same way.
            ContainerBridgePipeline.AllowBridgeHost(builder, endpoint);
            builder.Services.AddSingleton(new ContainerBridgeEndpointSource(endpoint));

            var bridgeOptions = Options.Create(new ContainerBridgeOptions
            {
                Enabled = true,
                Port = port
            });
            builder.Services.AddSingleton(bridgeOptions);

            // The real peer guard over the real watcher: a loopback client IS this computer, so the guard passing is
            // a fact about the guard rather than about a stand-in.
            var watcher = new ContainerBridgeAddressWatcher(bridgeOptions,
                new ManualTimeProvider(),
                NullLogger<ContainerBridgeAddressWatcher>.Instance,
                static () => []);
            builder.Services.AddSingleton(watcher);
            builder.Services.AddSingleton<ContainerBridgePeerGuardMiddleware>();

            var verifier = Substitute.For<IContainerBridgeTokenVerifier>();
            _ = verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ContainerBridgeCaller?)null);
            _ = verifier.VerifyAsync(validToken, Arg.Any<CancellationToken>()).Returns(new ContainerBridgeCaller
            {
                InstanceId = instanceId
            });
            builder.Services.AddSingleton(verifier);
            builder.Services.AddScoped<ContainerBridgeTokenMiddleware>();

            var models = Substitute.For<IGgufModelStore>();
            _ = models.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([
                new LocalModelDescriptor
                {
                    ModelName = ModelName,
                    ProviderName = "llama-server",
                    IsAvailable = true,
                    SizeBytes = 512_000_000,
                    ModifiedAt = DateTimeOffset.UnixEpoch,
                    MaxContextTokens = 32_768
                }
            ]));
            builder.Services.AddSingleton(models);
            builder.Services.AddSingleton(SubstitutedRuntimeOrchestration.Over(Substitute.For<ILlamaServerProcessSupervisor>()));
            builder.Services.AddHttpClient(LocalModelProxyForwarder.HttpClientName);
            builder.Services.AddScoped<LocalModelProxyForwarder>();

            var app = builder.Build();
            ContainerBridgePipeline.Map(app, endpoint);
            await app.StartAsync();

            var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}")
            };
            return new BridgeHost(app, client, watcher, models, validToken);
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            _watcher.Dispose();
            await _app.DisposeAsync();
        }

        // Bind :0, read what the kernel handed out, release it, and bind that number for real. The window between
        // the two binds is the reason this is not a general-purpose allocator, but Kestrel must know the port before
        // the branch predicate can be built, and a dynamic port cannot be known that early.
        private static int ReserveLoopbackPort()
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port: 0));
            return ((IPEndPoint)probe.LocalEndPoint!).Port;
        }
    }
}
