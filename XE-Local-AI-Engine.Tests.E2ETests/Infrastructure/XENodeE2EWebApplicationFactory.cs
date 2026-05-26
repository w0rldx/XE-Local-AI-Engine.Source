namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using OllamaSharp;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Ollama;
using XE_Local_AI_Engine.Testing.FakeOllama;

/// <summary>
///     Boots the XE worker-node host for browser-driven E2E tests on a real, pre-chosen
///     loopback port, serving the freshly built React dist same-origin from a temp web root.
///     Config block mirrors <c>XE-Local-AI-Engine.Tests.TestingWebAppFactory</c> (Testing env,
///     temp-file SQLite + key, local model provider + FakeOllama, no hosted services, unpaired
///     token store). Unlike the unit factory this binds a real socket (so a browser can connect)
///     and uses the local <see cref="StubTokenStore" /> instead of the shared unit-test mock.
///     No CORS is added — the host serves the SPA same-origin and rejects cross-origin XHR by design.
/// </summary>
public sealed class XENodeE2EWebApplicationFactory : WebApplicationFactory<Program>, IAsyncInitializer, IAsyncDisposable
{
    private static readonly SemaphoreSlim HostStartupLock = new(1, 1);

    /// <summary>Email of the admin user seeded into the node Identity DB for browser login in E2E.</summary>
    public const string AdminEmail = "e2e-admin@example.test";

    /// <summary>
    ///     Password of the seeded admin. Meets the node policy (>= 12 chars, upper/lower/digit/symbol).
    ///     Used by <c>XEE2ETestBase</c> to drive the real /login flow before each test.
    /// </summary>
    public const string AdminPassword = "E2eAdminPassw0rd!";

    private readonly FakeOllamaServer _fakeOllamaServer;

    // Populated either from the explicit ctor (spike / direct use) or from the injected
    // ReactClient fixture in InitializeAsync (the TUnit [ClassDataSource] path used by the base class).
    private int _port;
    private string _webRoot = string.Empty;

    /// <summary>
    ///     TUnit / base-class path: parameterless so it is constructible via
    ///     <c>[ClassDataSource]</c>. The port + web root come from the nested
    ///     <see cref="ReactClient" /> fixture, resolved in <see cref="InitializeAsync" />.
    /// </summary>
    public XENodeE2EWebApplicationFactory()
        : this(null)
    {
    }

    /// <param name="fakeOllamaOptions">Optional FakeOllama configuration; defaults to the standard test models.</param>
    public XENodeE2EWebApplicationFactory(FakeOllamaOptions? fakeOllamaOptions)
    {
        _fakeOllamaServer = FakeOllamaServer.StartAsync(fakeOllamaOptions ?? new FakeOllamaOptions
        {
            Models = ["qwen3.5:0.8b", "qwen3-embedding:0.6b"]
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Spike / direct-use path: the caller has already chosen the port and built the dist
    ///     (see <c>TokenInjectionSpikeE2ETests</c>, <c>HostBootSmokeE2ETests</c>).
    /// </summary>
    /// <param name="port">Free loopback port chosen up front (before the React build).</param>
    /// <param name="webRoot">Temp directory whose root <c>index.html</c> is the freshly built dist (UseWebRoot).</param>
    /// <param name="fakeOllamaOptions">Optional FakeOllama configuration; defaults to the standard test models.</param>
    public XENodeE2EWebApplicationFactory(int port, string webRoot, FakeOllamaOptions? fakeOllamaOptions = null)
        : this(fakeOllamaOptions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(webRoot);

        _port = port;
        _webRoot = webRoot;
    }

    /// <summary>
    ///     The freshly built React client. Injected by TUnit when this factory is itself a
    ///     <c>[ClassDataSource]</c>; null on the explicit-ctor path where port + web root are supplied directly.
    /// </summary>
    [ClassDataSource<XEReactClientFixture>(Shared = SharedType.PerTestSession)]
    public XEReactClientFixture? ReactClient { get; init; }

    /// <summary>The bound loopback address (e.g. <c>http://127.0.0.1:{port}</c>) resolved after the host starts.</summary>
    public string ServerAddress { get; private set; } = string.Empty;

    public new async ValueTask DisposeAsync()
    {
        await _fakeOllamaServer.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public async Task InitializeAsync()
    {
        // On the [ClassDataSource] path the port + web root come from the nested React fixture
        // (TUnit initializes nested data sources first, so its dist build has already run).
        if (_webRoot.Length == 0)
        {
            if (ReactClient is null)
            {
                throw new InvalidOperationException("XENodeE2EWebApplicationFactory requires either an explicit (port, webRoot) constructor " +
                                                    "or an injected ReactClient fixture.");
            }

            _port = ReactClient.Port;
            _webRoot = ReactClient.TempRoot;
        }

        // Bind a real Kestrel socket on the chosen port. Must be called before the host is
        // initialized (i.e. before Services is touched below) to take effect.
        UseKestrel(_port);

        // Touching Services forces host construction + socket bind so the address feature is populated.
        _ = Services;

        var server = Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>();
        ServerAddress = addressFeature?.Addresses.FirstOrDefault() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(ServerAddress))
        {
            throw new InvalidOperationException("Kestrel server address was not resolved during XE node E2E factory initialization.");
        }

        // Seed the single admin so the SPA presents /login (not the one-time /setup screen) and
        // browser tests can authenticate with a known password. Identity migrations + the Admin role
        // are applied by the host startup pipeline (Program.ApplyNodeIdentityMigrationsAsync) before
        // this runs. Idempotent: the PerTestSession factory initializes once per run.
        await SeedAdminUserAsync().ConfigureAwait(false);
    }

    private async Task SeedAdminUserAsync()
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>();

        if (await userManager.FindByEmailAsync(AdminEmail).ConfigureAwait(false) is not null)
        {
            return;
        }

        var admin = new NodeUser
        {
            Email = AdminEmail,
            UserName = AdminEmail,
            SetupCompleted = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        var createResult = await userManager.CreateAsync(admin, AdminPassword).ConfigureAwait(false);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to seed E2E admin user: " + string.Join(", ", createResult.Errors.Select(error => error.Description)));
        }

        var roleResult = await userManager.AddToRoleAsync(admin, NodeAuthorizationPolicies.AdminRole).ConfigureAwait(false);
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to assign Admin role to E2E admin user: " + string.Join(", ", roleResult.Errors.Select(error => error.Description)));
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Serialize host creation across parallel tests (matches TestingWebAppFactory).
        HostStartupLock.Wait();
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            HostStartupLock.Release();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The fresh dist as the web root so MapFallbackToFile + UseStaticFiles resolve it.
        // (The real socket on _port is wired via UseKestrel(port) in the constructor.)
        builder.UseWebRoot(_webRoot);

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:node-sqlite"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-e2e-{Guid.NewGuid():N}.sqlite")}",
                ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray()),
                ["XE_USE_LOCAL_MODEL_PROVIDER"] = "true",
                ["Ollama:ChatModel"] = "qwen3.5:0.8b"
            });
        });

        _ = builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.Configure<CentralPlatformOptions>(options =>
            {
                options.BaseUrl = "https://test.example.com";
            });

            services.RemoveAll<ITokenStore>();
            services.AddSingleton<ITokenStore>(_ => new StubTokenStore());

            services.RemoveAll<IDeadLetterStore>();
            services.AddSingleton<IDeadLetterStore>(_ => Substitute.For<IDeadLetterStore>());

            // Replace the real WorkerEventDispatcher with an inert stub so that
            // IWorkerEventDispatcher.CurrentInvocation always returns null.
            // Without this, a Chat e2e test that successfully sends a message sets
            // CurrentInvocation on the shared singleton, causing InvocationsPageE2ETests
            // to see an active invocation and fail the empty-state assertion.
            // NSubstitute's default for a reference-typed property is null — no extra
            // setup required.
            services.RemoveAll<IWorkerEventDispatcher>();
            services.AddSingleton<IWorkerEventDispatcher>(_ => Substitute.For<IWorkerEventDispatcher>());

            services.RemoveAll<IOllamaApiClient>();
            services.RemoveAll<IChatClient>();
            services.RemoveAll<ILocalModelProvider>();
            services.AddSingleton<IOllamaApiClient>(_ => new OllamaApiClient(_fakeOllamaServer.BaseAddress));
            services.AddSingleton<ILocalModelProvider, OllamaLocalModelProvider>();
            services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<ILocalModelProvider>().CreateChatClient(new LocalModelSelection
            {
                ModelName = "qwen3.5:0.8b",
                ProviderName = OllamaLocalModelProvider.OllamaProviderName
            }));

            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(_ => Substitute.For<IHttpClientFactory>());
        });

        base.ConfigureWebHost(builder);
    }
}
