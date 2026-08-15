namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using System.Text.Json;
using System.Text.RegularExpressions;
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
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Testing.FakeOllama;

/// <summary>
///     Boots the XE worker-node host for browser-driven E2E tests on a real, pre-chosen
///     loopback port, serving the freshly built React dist same-origin from a temp web root.
///     Config block mirrors <c>XE-Local-AI-Engine.Tests.TestServerWebAppFactory</c> (Testing env,
///     temp-file SQLite + key, local model provider + FakeOllama, no hosted services, unpaired
///     token store). Unlike the unit factory this binds a real socket (so a browser can connect)
///     and uses the local <see cref="StubTokenStore" /> instead of the shared unit-test mock.
///     No CORS is added — the host serves the SPA same-origin and rejects cross-origin XHR by design.
/// </summary>
public sealed class XENodeE2EWebApplicationFactory : WebApplicationFactory<Program>, IAsyncInitializer, IAsyncDisposable
{
    /// <summary>Email of the admin user seeded into the node Identity DB for browser login in E2E.</summary>
    public const string AdminEmail = "e2e-admin@example.test";

    /// <summary>
    ///     Password of the seeded admin. Meets the node policy (>= 12 chars, upper/lower/digit/symbol).
    ///     Used by <c>XEE2ETestBase</c> to drive the real /login flow before each test.
    /// </summary>
    public const string AdminPassword = "E2eAdminPassw0rd!";

    /// <summary>
    ///     Tutorial persistence keys mirrored from the central React <c>TutorialRegistry</c>. They must stay in step
    ///     or the seeded state below stops suppressing first-use invitations.
    ///     <para>
    ///         Enforced, not merely documented: <c>XE-Local-AI-Engine.Tests/Onboarding/OnboardingTourKeyDriftTests</c>
    ///         reads the TypeScript source and fails if either side moves. That guard lives in the UNIT suite so
    ///         drift is caught by the normal test run rather than only under <c>-p:RunE2ETests=true</c>.
    ///     </para>
    /// </summary>
    public const string MainAppTourKey = "main-app-v1";

    public const string AgentsTourKey = "agents-v1";

    public const string KnowledgeBaseTourKey = "knowledge-base-v1";

    /// <summary>
    ///     The browser localStorage key holding the versioned in-progress tour step id. Mirrors
    ///     <c>tutorialProgressStorageKey</c> in <c>useTourState.ts</c>.
    ///     Derived here from <see cref="MainAppTourKey" /> for the same reason it is derived there — so the tour key
    ///     exists in exactly one place per language, and the drift guard has one prefix left to check rather than a
    ///     second flattened copy of the key.
    /// </summary>
    public const string TourProgressStorageKey = $"xe-onboarding-{MainAppTourKey}-step";

    private static readonly SemaphoreSlim HostStartupLock = new(initialCount: 1, maxCount: 1);

    // Same options NodeTutorialStateService uses to (de)serialize NodeUser.TutorialState, so the seeded payload is
    // byte-compatible with what the production service writes and reads (camelCase property names).
    private static readonly JsonSerializerOptions TutorialStateSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly FakeOllamaServer _fakeOllamaServer;
    private readonly string _fixtureDataRoot = Path.Combine(Path.GetTempPath(), "xe-local-ai-engine-e2e-" + Guid.NewGuid().ToString("N"));

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
        Directory.CreateDirectory(_fixtureDataRoot);
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

    /// <summary>
    ///     Mutable FakeOllama state; tests may set <c>ToolCallScript</c> (or <c>ChatScript</c>)
    ///     before sending a message to control what the fake model returns.  Always reset to
    ///     <c>null</c> in a <c>[After(Test)]</c> hook (or at the start of the test) so the shared
    ///     <see cref="SharedType.PerTestSession" /> instance does not leak scripts across tests.
    /// </summary>
    public FakeOllamaState FakeOllamaState => _fakeOllamaServer.State;

    public new async ValueTask DisposeAsync()
    {
        await _fakeOllamaServer.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        TryDeleteDirectory(_fixtureDataRoot);
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

        // Diagnostic: verify the React bundle baked in the correct API URL.
        var bundlePortInDist = FindBundledApiUrl(_webRoot);
        await Console.Out.WriteLineAsync($"[FACTORY-DIAG] port={_port} ServerAddress={ServerAddress} webRoot={_webRoot} bundledApiUrl={bundlePortInDist}")
                     .ConfigureAwait(false);

        // Seed the single admin so the SPA presents /login (not the one-time /setup screen) and
        // browser tests can authenticate with a known password. Identity migrations + the Admin role
        // are applied by the host startup pipeline (Program.ApplyNodeIdentityMigrationsAsync) before
        // this runs. Idempotent: the PerTestSession factory initializes once per run.
        await SeedAdminUserAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Overwrites the seeded admin's persisted onboarding state, exactly as the production
    ///     <c>NodeTutorialStateService</c> would. Pass <see langword="null" /> to make the admin a genuine
    ///     first-run user again (the welcome prompt then fires), or
    ///     <see cref="CompletedMainAppTourState" /> to restore the default returning-user fixture.
    ///     <para>
    ///         Safe to call mid-suite because browser E2E runs strictly sequentially
    ///         (<c>BrowserParallelLimit.Limit == 1</c>); no other test can observe the intermediate state.
    ///     </para>
    /// </summary>
    public async Task SetAdminTutorialStateAsync(string? tutorialStateJson)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>();

        var admin = await userManager.FindByEmailAsync(AdminEmail).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"E2E admin '{AdminEmail}' was not seeded; cannot set tutorial state.");

        admin.TutorialState = tutorialStateJson;

        var updateResult = await userManager.UpdateAsync(admin).ConfigureAwait(false);
        if (!updateResult.Succeeded)
        {
            throw new InvalidOperationException("Failed to set E2E admin tutorial state: " +
                                                string.Join(", ", updateResult.Errors.Select(static error => error.Description)));
        }
    }

    /// <summary>
    ///     Reads back the seeded admin's raw persisted onboarding state so a test can assert what the app
    ///     actually wrote to the identity row (rather than trusting client-side state).
    /// </summary>
    public async Task<string?> GetAdminTutorialStateAsync()
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>();

        var admin = await userManager.FindByEmailAsync(AdminEmail).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"E2E admin '{AdminEmail}' was not seeded; cannot read tutorial state.");

        return admin.TutorialState;
    }

    /// <summary>
    ///     The persisted onboarding state that marks the main app tour as already completed — the DEFAULT
    ///     fixture state (see <see cref="SeedAdminUserAsync" />).
    ///     <para>
    ///         Every E2E run gets a fresh database and therefore a brand-new admin, for whom the first-run
    ///         welcome modal legitimately opens over the whole app and swallows every click. Seeding a
    ///         terminal tour outcome makes the standard fixture a RETURNING user: the app takes exactly the
    ///         code path a real returning user takes, with no test-only branch in shipping code and no
    ///         per-test UI dismissal. Coverage for the first-run path itself lives in
    ///         <c>OnboardingTourE2ETests</c>, which clears this state for the duration of one test.
    ///     </para>
    /// </summary>
    public static string CompletedMainAppTourState { get; } = JsonSerializer.Serialize(new[]
        {
            new StoredTutorialEntry
            {
                Key = MainAppTourKey,
                Status = "completed",
                AtUtc = new DateTime(year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0, DateTimeKind.Utc)
            },
            new StoredTutorialEntry
            {
                Key = AgentsTourKey,
                Status = "completed",
                AtUtc = new DateTime(year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0, DateTimeKind.Utc)
            },
            new StoredTutorialEntry
            {
                Key = KnowledgeBaseTourKey,
                Status = "completed",
                AtUtc = new DateTime(year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0, DateTimeKind.Utc)
            }
        },
        TutorialStateSerializerOptions);

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
            // Deterministic default: a returning user who has already answered the onboarding tour.
            // See CompletedMainAppTourState for why the first-run prompt must not be the E2E baseline.
            TutorialState = CompletedMainAppTourState,
            CreatedAtUtc = DateTime.UtcNow
        };

        var createResult = await userManager.CreateAsync(admin, AdminPassword).ConfigureAwait(false);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException("Failed to seed E2E admin user: " + string.Join(", ", createResult.Errors.Select(error => error.Description)));
        }

        var roleResult = await userManager.AddToRoleAsync(admin, NodeAuthorizationPolicies.AdminRole).ConfigureAwait(false);
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException("Failed to assign Admin role to E2E admin user: " + string.Join(", ", roleResult.Errors.Select(error => error.Description)));
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Serialize host creation across parallel tests (matches TestServerWebAppFactory).
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
        builder.UseSetting("Development:Enabled", "true");
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:node-sqlite"] = $"Data Source={Path.Combine(_fixtureDataRoot, "node.sqlite")}",
                ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray()),
                ["XE_USE_LOCAL_MODEL_PROVIDER"] = "true",
                ["Ollama:ChatModel"] = "qwen3.5:0.8b",
                ["KnowledgeBase:EmbeddingProviderName"] = "ollama",
                ["KnowledgeBase:EmbeddingModelName"] = "qwen3-embedding:0.6b",
                ["NodeData:Directory"] = Path.Combine(_fixtureDataRoot, "node-data"),
                ["Development:Enabled"] = "true"
            });
        });

        _ = builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            // Most production workers are intentionally absent from browser E2E, but knowledge imports are defined as
            // queue-backed operations. Keep the real ingestion worker so the focused RAG scenario exercises the same
            // endpoint -> bounded dispatcher -> scoped ingestion pipeline as production instead of calling ingestion
            // directly from test code. The scheduled corpus reindex worker remains disabled.
            services.AddHostedService<KnowledgeIngestionWorker>();

            services.Configure<CentralPlatformOptions>(options =>
            {
                options.BaseUrl = "https://test.example.com";
            });

            services.RemoveAll<ITokenStore>();
            services.AddSingleton<ITokenStore>(_ => new StubTokenStore());

            services.RemoveAll<IDeadLetterStore>();
            services.AddSingleton<IDeadLetterStore>(_ => Substitute.For<IDeadLetterStore>());

            services.RemoveAll<IDevelopmentCoderModel>();
            services.AddSingleton<IDevelopmentCoderModel, DevelopmentE2ECoderModel>();
            services.RemoveAll<IDevelopmentReviewerModel>();
            services.AddSingleton<IDevelopmentReviewerModel, DevelopmentE2EReviewerModel>();

            // There is deliberately NO IOptions<DevelopmentOptions> override here any more, and re-adding one would be
            // a regression rather than a fix.
            //
            // This suite used to register a closed-generic IOptions<DevelopmentOptions> narrowing
            // DevelopmentOptions.ValidationCommandIds to [git_diff_check], because DevelopmentWorkflowE2ETests drives
            // a synthetic temp git repository holding a README and nothing else, and the global production default
            // (git_diff_check + dotnet restore/build/test against a hardcoded XE-Local-AI-Engine.slnx) could never
            // succeed there. That override existed to work around a global default that assumed one specific
            // repository.
            //
            // The validation command list now lives on the project's own command profile, so there is no global
            // default left to narrow. The README fixture has no .NET build system, so profile detection resolves it to
            // the code-owned "generic-git" profile whose validation list is exactly [git_diff_check] — the same
            // coverage this override used to force, now reached honestly and recorded on every artifact the gate
            // produces instead of being hidden in test DI.
            //
            // The original warning still stands in its new form: do NOT "fix" a red Development E2E by weakening a
            // shared profile in DevelopmentCommandProfileCatalog. This test owns the UI state machine (register ->
            // create -> code -> validate -> review -> hash-bound apply); the content of the gate is owned by
            // DevelopmentValidationReviewAndApplyTests, which drives DevelopmentValidationRunner against a real
            // buildable solution under a real dotnet profile and proves that a patch which breaks the build and a
            // patch which fails a test each keep the attempt out of InReview.

            // The REAL WorkerEventDispatcher from the app's DI stands here (in-memory, no hosted
            // service) so it raises InvocationStateChanged and processes ReportInvocationCompletedAsync —
            // the terminal Completed state then reaches NodeChatStreamService's pump and the local
            // assistant reply persists as `completed` (not `interrupted`), keeping regenerate/branch/
            // feedback actions visible. CurrentInvocation leaking into InvocationsPageE2ETests'
            // empty-state assertion (this dispatcher is shared via PerTestSession and never self-resets)
            // is prevented by per-test isolation: XEE2ETestBase calls WorkerEventDispatcher.ResetForTests()
            // in a [Before(Test)] hook.

            services.RemoveAll<IOllamaApiClient>();
            services.RemoveAll<IChatClient>();
            services.RemoveAll<ILocalModelProvider>();
            // Repoint the shared Ollama transport factory (production registered one at the real endpoint) at the fake
            // server, so the provider's per-model CreateChatClient/CreateEmbeddingGenerator — which now mint clients
            // through this factory — reach FakeOllama.
            services.RemoveAll<OllamaApiClientFactory>();
#pragma warning disable CA2000 // The factory singleton owns and disposes this HttpClient for the test host lifetime.
            services.AddSingleton(_ => new OllamaApiClientFactory(new HttpClient
            {
                BaseAddress = _fakeOllamaServer.BaseAddress
            }, ownsHttpClient: true));
#pragma warning restore CA2000
            services.AddSingleton<IOllamaApiClient>(_ => new OllamaApiClient(_fakeOllamaServer.BaseAddress));
            services.AddSingleton<ILocalModelProvider, OllamaLocalModelProvider>();
            services.AddSingleton<ILocalModelProvider>(_ =>
            {
                var llamacpp = Substitute.For<ILocalModelProvider>();
                llamacpp.ProviderName.Returns("llamacpp");
                return llamacpp;
            });
            // Register the base IChatClient pointing directly at FakeOllama, then re-apply the full
            // agent pipeline decoration (ToolInvocationObservabilityChatClient + UseFunctionInvocation)
            // so tool-call lifecycle events reach the SignalR stream (chat-tool-call-group in the UI).
            var fakeOllamaBase = _fakeOllamaServer.BaseAddress;
            services.AddSingleton<IChatClient>(_ => new OllamaApiClient(fakeOllamaBase, "qwen3.5:0.8b"));
            services.DecorateChatClientPipeline();

            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(_ =>
            {
                // Return a real (but un-routed) HttpClient for ANY named client so DI factories that construct an
                // HttpClient at resolve time (e.g. the Hugging Face discovery/download clients reached by the model-fit
                // advisor endpoints, instantiated by FastEndpoints at MapFastEndpoints/startup) can be built. No real
                // request is made in these E2E flows — the consuming endpoints catch transport failures and degrade —
                // so this never performs network I/O. Mirrors TestServerWebAppFactory's unit-side factory.
                var factory = Substitute.For<IHttpClientFactory>();
                factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
                return factory;
            });
        });

        base.ConfigureWebHost(builder);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort E2E cleanup; temporary data is reclaimed by the operating system.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort E2E cleanup; temporary data is reclaimed by the operating system.
        }
    }

    /// <summary>
    ///     Scans the first JS asset in the web root for a baked <c>127.0.0.1:{port}</c> pattern,
    ///     returning the matched URL or <c>"(not found)"</c> for diagnostics.
    /// </summary>
    private static string FindBundledApiUrl(string webRoot)
    {
        try
        {
            var assetsDir = Path.Combine(webRoot, "assets");
            if (!Directory.Exists(assetsDir))
            {
                return "(no assets dir)";
            }

            foreach (var jsFile in Directory.EnumerateFiles(assetsDir, "*.js"))
            {
                var content = File.ReadAllText(jsFile);
                var match = Regex.Match(content, @"127\.0\.0\.1:\d+");
                if (match.Success)
                {
                    return match.Value;
                }
            }

            return "(no 127.0.0.1 match in assets)";
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }
    }

    /// <summary>
    ///     Wire shape of one persisted tutorial entry. Mirrors the private <c>StoredEntry</c> record inside
    ///     <c>NodeTutorialStateService</c> — same property names, same <c>JsonSerializerDefaults.Web</c>
    ///     casing — so the seeded column value round-trips through the real service unchanged.
    /// </summary>
    private sealed record StoredTutorialEntry
    {
        public string? Key { get; init; }

        public string? Status { get; init; }

        public DateTime AtUtc { get; init; }
    }
}
