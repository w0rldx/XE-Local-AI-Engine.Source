namespace XE_Local_AI_Engine.Tests;

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
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
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     The host fixture for this module: builds the app directly through <see cref="Program.CreateAppAsync" /> and serves
///     it on TestServer — no <c>WebApplicationFactory&lt;Program&gt;</c>, no entry-point resolution, and therefore no
///     <c>HostFactoryResolver.HostingListener</c> thread parked in <c>app.Run()</c> whose AsyncLocal roots every built
///     host for the process lifetime (docs/agent-knowledge.md §1, dotnet/aspnetcore#48047). It replaced the former
///     <c>TestingWebAppFactory</c>, whose per-host leak was unfixable from the fixture.
/// </summary>
public sealed class TestServerWebAppFactory : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    ///     Fixed security stamp bound into the synthetic operator token by <see cref="CreateNodeAccessToken" />. Tests that
    ///     ALSO persist the <c>node-admin-test</c> Identity row must seed this exact value so the JWT validator's
    ///     fail-closed stamp check matches; tests that do not persist a user authenticate via the validator's
    ///     user-not-found pass-through.
    /// </summary>
    public const string NodeAdminTestSecurityStamp = "node-admin-test-security-stamp";

    private const string ReactShellFixtureHtml =
        "<!doctype html>\n<html lang=\"en\">\n<head><meta charset=\"utf-8\"><title>XE Local AI Engine</title></head>\n" +
        "<body><div id=\"root\"></div><script type=\"module\" src=\"/assets/index.js\"></script></body>\n</html>\n";

    private readonly FakeOllamaServer? _fakeOllamaServer;
    private readonly string _fixtureWebRoot;
    private readonly string _nodeSqlitePath;
    private readonly string _nodeDataDirectory;
    private readonly HttpClient _offlineRuntimeHttpClient = new(new OfflineRuntimeHandler(), disposeHandler: true);

    // Process-wide: see the comment in EnsureApp.
    private static readonly SemaphoreSlim HostStartupLock = new(initialCount: 1, maxCount: 1);

    private readonly Lock _appGate = new();
    private WebApplication? _app;
    private bool _disposed;

    // TUnit's ClassDataSource<T> resolves the fixture through a new() constraint, which an all-optional-parameter
    // constructor does not satisfy (TUnit0061), so the parameterless form is declared explicitly.
    public TestServerWebAppFactory()
        : this(fakeOllamaOptions: null)
    {
    }

    public TestServerWebAppFactory(FakeOllamaOptions? fakeOllamaOptions)
    {
        _fixtureWebRoot = Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-wwwroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixtureWebRoot);
        File.WriteAllText(Path.Combine(_fixtureWebRoot, "index.html"), ReactShellFixtureHtml);
        _nodeSqlitePath = Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-{Guid.NewGuid():N}.sqlite");
        _nodeDataDirectory = Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-nodedata-{Guid.NewGuid():N}");

        if (!RunLocalIntegration)
        {
            _fakeOllamaServer = FakeOllamaServer.StartAsync(fakeOllamaOptions ?? new FakeOllamaOptions
            {
                Models = ["qwen3.5:0.8b", "qwen3-embedding:0.6b"]
            }).GetAwaiter().GetResult();
        }
    }

    public bool SkipDefaultBaseUrlOverride { get; init; }

    public bool? EnableDevelopmentMode { get; init; }

    public Action<IServiceCollection>? ConfigureAdditionalTestServices { get; init; }

    // Last-wins overlay on the fixture's own configuration block, replacing the WebApplicationFactory-era
    // WithWebHostBuilder(b => b.ConfigureAppConfiguration(...)) re-configuration.
    public IReadOnlyDictionary<string, string?>? AdditionalConfiguration { get; init; }

    public IServiceProvider Services => EnsureApp().Services;

    // The host's TestServer, for tests that need CreateHandler() to point a SignalR client at the in-memory transport.
    public TestServer Server => EnsureApp().GetTestServer();

    private static bool RunLocalIntegration =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public HttpClient CreateClient() => EnsureApp().GetTestClient();

    public string CreateNodeAccessToken()
    {
        var tokenService = Services.GetRequiredService<INodeTokenService>();
        var user = new NodeUser
        {
            Id = "node-admin-test",
            UserName = "admin@example.test",
            Email = "admin@example.test",
            SetupCompleted = true,
            SecurityStamp = NodeAdminTestSecurityStamp
        };

        return tokenService.CreateAccessToken(user, [NodeAuthorizationPolicies.AdminRole]).AccessToken;
    }

    public void AddNodeBearerToken(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Authorization = new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, CreateNodeAccessToken());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // WebApplicationFactory.DisposeAsync was idempotent and some tests dispose explicitly on top of `await using`.
        lock (_appGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // A throwing host stop must still propagate (the test should fail loudly), but every cleanup stage below runs
        // regardless: _disposed is already latched, so a skipped stage would never get a second chance and the temp
        // artifacts would accumulate across the run (docs/agent-knowledge.md §1 — this once filled the 16 GB tmpfs).
        try
        {
            if (_app is { } app)
            {
                await app.StopAsync().ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (_fakeOllamaServer is not null)
                {
                    await _fakeOllamaServer.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _offlineRuntimeHttpClient.Dispose();

                // Microsoft.Data.Sqlite keeps a STATIC pool group per connection string; this host's unique temp DB
                // path would otherwise leave an immortal pool (open connection + prune timer) behind. Clearing all
                // pools (public API) is safe here: batch runs are single-threaded, and a concurrent host merely
                // reopens its pooled connection.
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                TryDeleteDirectory(_fixtureWebRoot);
                TryDeleteDirectory(_nodeDataDirectory);
                TryDeleteSqliteFamily(_nodeSqlitePath);
            }
        }
    }

    // Lazy sync-over-async build on first use, matching WebApplicationFactory's lazy host start. TUnit has no
    // synchronization context, so blocking here cannot deadlock.
    private WebApplication EnsureApp()
    {
        lock (_appGate)
        {
            if (_app is not null)
            {
                return _app;
            }

            var configuration = new Dictionary<string, string?>
            {
                ["ConnectionStrings:node-sqlite"] = $"Data Source={_nodeSqlitePath}",
                ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray()),
                ["XE_USE_LOCAL_MODEL_PROVIDER"] = "true",
                ["Ollama:ChatModel"] = "qwen3.5:0.8b",
                ["NodeData:Directory"] = _nodeDataDirectory,
                // Keep this host out of EF's static ServiceProviderCache: its per-host connection string would add an
                // immortal cache entry strongly rooting this host's whole ServiceProvider (the measured ~20 MB/host
                // leak — see the EnableServiceProviderCaching seam in AddNodeModelRuntimeExtensions).
                ["EntityFramework:ServiceProviderCaching"] = "false"
            };
            if (EnableDevelopmentMode is { } developmentEnabled)
            {
                configuration["Development:Enabled"] = developmentEnabled.ToString();
            }

            if (AdditionalConfiguration is { } additionalConfiguration)
            {
                foreach (var entry in additionalConfiguration)
                {
                    configuration[entry.Key] = entry.Value;
                }
            }

            // Host bootstrap is not re-entrant (docs/wiki/13-testing-and-validation.md — TUnit runs classes in
            // parallel), and CreateAppAsync additionally mutates the global Serilog Log.Logger. Serialize app creation
            // AND startup so a normal parallel TUnit run cannot race two bootstraps; steady-state requests are
            // unaffected.
            HostStartupLock.Wait();
            try
            {
                var start = Program.CreateAppAsync([], new ProgramAppCustomization
                {
                    EnvironmentName = "Testing",
                    ContentRootPath = ResolveClientContentRoot(),
                    WebRootPath = _fixtureWebRoot,
                    Configuration = configuration,
                    ConfigureBuilder = builder =>
                    {
                        builder.WebHost.UseTestServer();
                        ConfigureTestServices(builder.Services);
                    }
                }).GetAwaiter().GetResult();

                var app = start.App ?? throw new InvalidOperationException($"CreateAppAsync early-exited with code {start.ExitCode}.");
                app.StartAsync().GetAwaiter().GetResult();
                _app = app;
                return app;
            }
            finally
            {
                HostStartupLock.Release();
            }
        }
    }

    // The same content root WebApplicationFactory resolves: the Client project's source directory, taken from the
    // MvcTestingAppManifest.json that Microsoft.AspNetCore.Mvc.Testing writes into the test output. Fail loud if the
    // manifest or entry is missing — a silently wrong content root would load no appsettings.json.
    internal static string ResolveClientContentRoot()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "MvcTestingAppManifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return manifest.RootElement.GetProperty(typeof(Program).Assembly.FullName!).GetString()
               ?? throw new InvalidOperationException("MvcTestingAppManifest.json has a null Client content root.");
    }

    // The test host's deviations from the product composition: no background services, a temp-scoped Data Protection
    // ring, runtime-acquisition seams pointed at a transport that always fails (so no host build can reach GitHub), an
    // unpaired token store, and — off RUN_LOCAL_INTEGRATION — the fake Ollama backend in place of a live model.
    private void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IHostedService>();

        services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_nodeDataDirectory, "dp-keys")));

        services.RemoveAll<IInstalledRuntimeStore>();
        services.AddSingleton<IInstalledRuntimeStore>(_ => new InstalledRuntimeStore(_nodeDataDirectory));
        services.RemoveAll<ILlamaCppReleaseCatalog>();
        services.AddSingleton<ILlamaCppReleaseCatalog>(_ => new GitHubLlamaCppReleaseCatalog(_offlineRuntimeHttpClient));
        services.RemoveAll<ILlamaCppBinaryManager>();
        services.AddSingleton<ILlamaCppBinaryManager>(sp => new LlamaCppBinaryManager(_offlineRuntimeHttpClient,
            _nodeDataDirectory,
            activeTag: null,
            sp.GetRequiredService<ILlamaCppReleaseCatalog>(),
            sp.GetRequiredService<IInstalledRuntimeStore>(),
            sp.GetRequiredService<LlamaServerRuntimeOverrideOptions>(),
            sp.GetRequiredService<ICudaManagedBuildSignal>()));

        if (!SkipDefaultBaseUrlOverride)
        {
            services.Configure<CentralPlatformOptions>(options =>
            {
                options.BaseUrl = "https://test.example.com";
            });
        }

        services.RemoveAll<ITokenStore>();
        services.AddSingleton<ITokenStore>(_ => MockTokenStore.Unpaired());

        services.RemoveAll<IDeadLetterStore>();
        services.AddSingleton<IDeadLetterStore>(_ => Substitute.For<IDeadLetterStore>());

        if (!RunLocalIntegration)
        {
            services.RemoveAll<IOllamaApiClient>();
            services.RemoveAll<IChatClient>();
            services.RemoveAll<ILocalModelProvider>();
            services.RemoveAll<OllamaApiClientFactory>();
#pragma warning disable CA2000 // The factory singleton owns and disposes this HttpClient for the test host lifetime.
            services.AddSingleton(_ => new OllamaApiClientFactory(new HttpClient
            {
                BaseAddress = _fakeOllamaServer!.BaseAddress
            }, ownsHttpClient: true));
#pragma warning restore CA2000
            services.AddSingleton<IOllamaApiClient>(_ => new OllamaApiClient(_fakeOllamaServer!.BaseAddress));
            services.AddSingleton<ILocalModelProvider, OllamaLocalModelProvider>();
            services.AddSingleton<ILocalModelProvider>(_ =>
            {
                var llamacpp = Substitute.For<ILocalModelProvider>();
                llamacpp.ProviderName.Returns("llamacpp");
                return llamacpp;
            });
            services.AddSingleton<IChatClient>(sp => sp.GetServices<ILocalModelProvider>()
                                                       .First(provider => provider.ProviderName == OllamaLocalModelProvider.OllamaProviderName)
                                                       .CreateChatClient(new LocalModelSelection
                                                       {
                                                           ModelName = "qwen3.5:0.8b",
                                                           ProviderName = OllamaLocalModelProvider.OllamaProviderName
                                                       }));

            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(_ =>
            {
                var factory = Substitute.For<IHttpClientFactory>();
                factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
                return factory;
            });
        }

        ConfigureAdditionalTestServices?.Invoke(services);
    }

    private sealed class OfflineRuntimeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("The unit-test host has no llama.cpp runtime transport."));
        }
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
            // Best-effort temp cleanup; a locked/racing file is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; ignore.
        }
    }

    private static void TryDeleteSqliteFamily(string sqlitePath)
    {
        var directory = Path.GetDirectoryName(sqlitePath);
        var prefix = Path.GetFileName(sqlitePath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(prefix))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Best-effort temp cleanup; ignore.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort temp cleanup; ignore.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Temp directory already gone; nothing to clean.
        }
    }
}
