namespace XE_Local_AI_Engine.Tests.Containers.Bridge;

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The regression guard for the defect a live debugging round found: host filtering runs from an
///     <c>IStartupFilter</c>, ahead of every middleware the composition root registers, and the shipped
///     <c>AllowedHosts</c> is loopback-only — so without <see cref="ContainerBridgePipeline.AllowBridgeHost" /> a
///     container's <c>Host: 172.20.0.1:18790</c> is answered 400 by a filter nothing in the bridge can see.
///     <para>
///         <c>ContainerBridgePipelineTests</c> cannot catch that. Both bridge fixtures build with
///         <c>WebApplication.CreateSlimBuilder()</c>, which deliberately omits the host-filtering startup filter, and
///         <c>XE-Local-AI-Engine.Tests</c> ships no <c>appsettings.json</c>, so <c>AllowedHosts</c> is unset there and
///         the host-filtering default for unset is <c>*</c>. Deleting the widening left the whole suite green.
///     </para>
///     <para>
///         So this class puts the real <c>HostFilteringMiddleware</c> in front of the real bridge branch, seeded with
///         the real shipped allow list, and runs the same request with the widening and without it. The
///         <see cref="AllowBridgeHost_IsCalledByTheCompositionRoot" /> test is what ties the proof to the product:
///         the fixture has to call the widening itself, so deleting the call from <c>Program.cs</c> alone would not
///         otherwise be visible.
///     </para>
/// </summary>
public sealed class ContainerBridgeHostFilteringTests
{
    /// <summary>The value the node ships. Loopback-only, which is exactly right for every listener but the bridge.</summary>
    private const string ShippedAllowedHosts = "localhost;127.0.0.1;[::1]";

    /// <summary>A container-network address for the host, which is what the bridge binds on a real node and what no shipped allow list contains.</summary>
    private const string BridgeAddress = "172.20.0.1";

    [Test]
    public void AllowBridgeHost_KeepsTheShippedNamesAndAddsTheBoundAddress()
    {
        var widened = WidenedAllowedHosts(new ResolvedContainerBridgeEndpoint(IPAddress.Parse(BridgeAddress), 18790, $"{BridgeAddress}:18790"));

        AssertEx.Equal($"localhost;127.0.0.1;[::1];{BridgeAddress}", widened,
            "The shipped loopback names must survive the widening: they are what every other listener is reached by.");
    }

    /// <summary>
    ///     On Docker Desktop the container-facing name is an alias rather than the bound address, and the alias is
    ///     what arrives in the Host header. Both spellings have to be allowed, because the same node may be reached
    ///     either way.
    /// </summary>
    [Test]
    public void AllowBridgeHost_OnDockerDesktop_AlsoAddsTheAlias()
    {
        var widened = WidenedAllowedHosts(new ResolvedContainerBridgeEndpoint(IPAddress.Parse(BridgeAddress), 18790, "host.docker.internal:18790"));

        AssertEx.Equal($"localhost;127.0.0.1;[::1];{BridgeAddress};host.docker.internal", widened,
            "A Docker Desktop container dials the alias, so the alias is what arrives in the Host header.");
    }

    /// <summary>
    ///     Called twice — as a host built more than once in a process would — the list must not grow. The widening
    ///     writes a provider rather than a key, so a duplicate would otherwise accumulate silently.
    /// </summary>
    [Test]
    public void AllowBridgeHost_CalledTwice_AddsNoDuplicate()
    {
        var endpoint = new ResolvedContainerBridgeEndpoint(IPAddress.Parse(BridgeAddress), 18790, $"{BridgeAddress}:18790");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("AllowedHosts", ShippedAllowedHosts)]);

        ContainerBridgePipeline.AllowBridgeHost(builder, endpoint);
        ContainerBridgePipeline.AllowBridgeHost(builder, endpoint);

        AssertEx.Equal($"localhost;127.0.0.1;[::1];{BridgeAddress}", ReadAllowedHosts(builder.Configuration),
            "A second call must be a no-op; the widening writes a provider, so a duplicate would accumulate unseen.");
    }

    /// <summary>
    ///     The widening has to survive a configuration reload, so it is written as its own highest-precedence
    ///     provider rather than through the indexer — which sets the key on every provider and lets a reloading JSON
    ///     file win it back.
    /// </summary>
    [Test]
    public void AllowBridgeHost_SurvivesAReloadOfTheFileItWidened()
    {
        var reloadable = new MemoryConfigurationSourceStub(ShippedAllowedHosts);
        var builder = WebApplication.CreateSlimBuilder();
        ((IConfigurationBuilder)builder.Configuration).Add(reloadable);

        ContainerBridgePipeline.AllowBridgeHost(builder,
            new ResolvedContainerBridgeEndpoint(IPAddress.Parse(BridgeAddress), 18790, $"{BridgeAddress}:18790"));
        reloadable.Reload();

        AssertEx.Contains(ReadAllowedHosts(builder.Configuration), BridgeAddress, message:
            "A reload of the underlying file must not take the bridge's own host name back out of the allow list.");
    }

    /// <summary>
    ///     The end-to-end statement, over a real socket with the real host filter: a container's Host header reaches
    ///     the bridge branch and is answered by the TOKEN gate (401), not by the host filter (400).
    /// </summary>
    [Test]
    public async Task Bridge_WithTheWidening_AcceptsAContainersHostHeader()
    {
        await using var host = await FilteredBridgeHost.StartAsync(widenAllowedHosts: true).ConfigureAwait(false);

        using var response = await host.GetAsync(ContainerBridgePipeline.ModelsPath).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode,
            "The request must reach the bridge's token gate; a 400 here means host filtering refused it first.");
        AssertEx.Equal("Bearer", AssertEx.NotNull(response.Headers.WwwAuthenticate.FirstOrDefault()).Scheme);
    }

    /// <summary>
    ///     The negative control, and the half that makes the positive one evidence: with the shipped allow list
    ///     alone, the identical request is refused 400 before any bridge middleware runs.
    /// </summary>
    [Test]
    public async Task Bridge_WithoutTheWidening_IsRefusedByHostFilteringBeforeAnyBridgeMiddleware()
    {
        await using var host = await FilteredBridgeHost.StartAsync(widenAllowedHosts: false).ConfigureAwait(false);

        using var response = await host.GetAsync(ContainerBridgePipeline.ModelsPath).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode,
            "Without the widening the shipped loopback-only allow list refuses the bridge's own Host header, which is the defect this guard exists for.");
    }

    /// <summary>
    ///     The fixture has to call <see cref="ContainerBridgePipeline.AllowBridgeHost" /> itself, so nothing above
    ///     would fail if the composition root stopped calling it. This reads the composition root's source and
    ///     asserts the call is there, the same raw-text technique
    ///     <c>ContainerBridgeLayeringArchitectureTests</c> uses for a rule the type graph cannot express.
    /// </summary>
    [Test]
    public void AllowBridgeHost_IsCalledByTheCompositionRoot()
    {
        var program = RepositoryPaths.ClientProject("Program.cs");
        AssertEx.True(File.Exists(program), $"'{program}' does not exist, so this guard reads nothing.");

        var source = File.ReadAllText(program);

        AssertEx.Contains(source, $"{nameof(ContainerBridgePipeline)}.{nameof(ContainerBridgePipeline.AllowBridgeHost)}(", message:
            "The bridge listener is unreachable without the AllowedHosts widening: host filtering runs ahead of every "
            + "middleware the composition root registers and refuses the bridge's own Host header with 400. Every "
            + "bridge test builds its own host, so this call going missing from Program.cs is invisible to all of them.");
    }

    private static string WidenedAllowedHosts(ResolvedContainerBridgeEndpoint endpoint)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("AllowedHosts", ShippedAllowedHosts)]);

        ContainerBridgePipeline.AllowBridgeHost(builder, endpoint);

        return ReadAllowedHosts(builder.Configuration);
    }

    private static string ReadAllowedHosts(IConfiguration configuration)
    {
        return configuration["AllowedHosts"] ?? string.Empty;
    }

    /// <summary>
    ///     An in-memory provider that can be told to reload, standing in for the reloading <c>appsettings.json</c>.
    ///     The real thing needs a file on disk and a file watcher; what is under test is provider precedence, and a
    ///     reload notification is the whole of what the file contributes to it.
    /// </summary>
    private sealed class MemoryConfigurationSourceStub : IConfigurationSource
    {
        private readonly string _allowedHosts;
        private Provider? _provider;

        public MemoryConfigurationSourceStub(string allowedHosts)
        {
            _allowedHosts = allowedHosts;
        }

        public void Reload()
        {
            _provider?.Reload();
        }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            _provider = new Provider(_allowedHosts);
            return _provider;
        }

        private sealed class Provider : ConfigurationProvider
        {
            private readonly string _allowedHosts;

            public Provider(string allowedHosts)
            {
                _allowedHosts = allowedHosts;
            }

            public override void Load()
            {
                Data["AllowedHosts"] = _allowedHosts;
            }

            public void Reload()
            {
                Load();
                OnReload();
            }
        }
    }

    /// <summary>
    ///     A real Kestrel listener with the REAL <c>HostFilteringMiddleware</c> in front of the real bridge branch.
    ///     Host filtering is wired exactly as <c>ConfigureWebDefaults</c> wires it — <c>HostFilteringOptions</c> bound
    ///     from the <c>AllowedHosts</c> configuration key — so what is proven is that the widening lands in the key
    ///     the filter actually reads.
    ///     <para>
    ///         The socket is loopback and the Host header is the bridge's address: host filtering decides on the
    ///         header alone, and a loopback peer is what lets the real peer guard pass so the token gate is the next
    ///         thing the request can meet.
    ///     </para>
    /// </summary>
    private sealed class FilteredBridgeHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly int _port;
        private readonly ContainerBridgeAddressWatcher _watcher;

        private FilteredBridgeHost(WebApplication app, HttpClient client, ContainerBridgeAddressWatcher watcher, int port)
        {
            _app = app;
            _client = client;
            _watcher = watcher;
            _port = port;
        }

        public static async Task<FilteredBridgeHost> StartAsync(bool widenAllowedHosts)
        {
            var port = ReserveLoopbackPort();

            // TWO endpoints, because the fixture separates what is under test from where the socket actually is.
            // The widening under test is about a LAN address a container would dial, so that is the endpoint
            // AllowBridgeHost is given. The socket itself is loopback — this process must not bind a LAN address to
            // run a test — so that is the endpoint the branch predicate is given, since the predicate matches the
            // whole local end of the connection. Host filtering decides on the Host HEADER alone, which is what lets
            // the two differ without weakening what is proven.
            var wideningEndpoint = new ResolvedContainerBridgeEndpoint(IPAddress.Parse(BridgeAddress), port, $"{BridgeAddress}:{port}");
            var listenerEndpoint = new ResolvedContainerBridgeEndpoint(IPAddress.Loopback, port, $"{BridgeAddress}:{port}");

            var builder = WebApplication.CreateSlimBuilder();
#pragma warning disable S5332 // The bridge is plain HTTP by design; this binds loopback in-process for one test.
            builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
#pragma warning restore S5332
            builder.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("AllowedHosts", ShippedAllowedHosts)]);

            if (widenAllowedHosts)
            {
                ContainerBridgePipeline.AllowBridgeHost(builder, wideningEndpoint);
            }

            // The framework's own binding of the key to the filter, so the key the product writes is the key the
            // filter reads. Anything else here would be this test agreeing with itself.
            var allowedHosts = builder.Configuration["AllowedHosts"];
            _ = builder.Services.Configure<HostFilteringOptions>(options =>
                options.AllowedHosts = [.. (allowedHosts ?? "*").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

            var bridgeOptions = Options.Create(new ContainerBridgeOptions { Enabled = true, Port = port });
            builder.Services.AddSingleton(bridgeOptions);
            builder.Services.AddSingleton(new ContainerBridgeEndpointSource(listenerEndpoint));

            var watcher = new ContainerBridgeAddressWatcher(bridgeOptions,
                new ManualTimeProvider(),
                NullLogger<ContainerBridgeAddressWatcher>.Instance,
                static () => []);
            builder.Services.AddSingleton(watcher);
            builder.Services.AddSingleton<ContainerBridgePeerGuardMiddleware>();

            var verifier = Substitute.For<IContainerBridgeTokenVerifier>();
            _ = verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ContainerBridgeCaller?)null);
            builder.Services.AddSingleton(verifier);
            builder.Services.AddScoped<ContainerBridgeTokenMiddleware>();

            var app = builder.Build();

            // FIRST, which is the whole point: on a real node the startup filter puts it ahead of everything the
            // composition root registers, and the bridge branch is one of those things.
            app.UseHostFiltering();
            ContainerBridgePipeline.Map(app, listenerEndpoint);
            await app.StartAsync().ConfigureAwait(false);

            var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            return new FilteredBridgeHost(app, client, watcher, port);
        }

        public async Task<HttpResponseMessage> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);

            // The header a container sends, independent of the loopback socket this test connects over.
            request.Headers.Host = $"{BridgeAddress}:{_port}";

            return await _client.SendAsync(request).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            _watcher.Dispose();
            await _app.DisposeAsync().ConfigureAwait(false);
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
