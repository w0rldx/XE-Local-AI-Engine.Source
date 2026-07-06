namespace XE_Local_AI_Engine.Tests;

using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
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
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public class TestingWebAppFactory : WebApplicationFactory<Program>, IAsyncInitializer, IAsyncDisposable
{
    private static readonly SemaphoreSlim HostStartupLock = new(initialCount: 1, maxCount: 1);

    // Minimal React-shell fixture served for the SPA fallback (MapFallbackToFile("index.html")). wwwroot/** is
    // gitignored, so a clean checkout / CI host has no built SPA and the fallback would 404 — pointing the test host's
    // web root at this fixture makes route-coexistence assertions (shell served at "/" and deep links, JSON at API/health
    // paths, 404 for missing /assets/*) hermetic without requiring a real `pnpm build`. It carries the markers the tests
    // assert on (a #root div + an /assets/ reference) and deliberately omits the Blazor shell script and the /app prefix.
    private const string ReactShellFixtureHtml =
        "<!doctype html>\n<html lang=\"en\">\n<head><meta charset=\"utf-8\"><title>XE Local AI Engine</title></head>\n" +
        "<body><div id=\"root\"></div><script type=\"module\" src=\"/assets/index.js\"></script></body>\n</html>\n";

    private readonly FakeOllamaServer? _fakeOllamaServer;
    private readonly string _fixtureWebRoot;

    public TestingWebAppFactory(FakeOllamaOptions? fakeOllamaOptions = null)
    {
        _fixtureWebRoot = CreateShellFixtureWebRoot();

        if (!RunLocalIntegration)
        {
            _fakeOllamaServer = FakeOllamaServer.StartAsync(fakeOllamaOptions ?? new FakeOllamaOptions
            {
                Models = ["qwen3.5:0.8b", "qwen3-embedding:0.6b"]
            }).GetAwaiter().GetResult();
        }
    }

    // Writes the React-shell fixture into a fresh temp directory used as the test host's web root.
    private static string CreateShellFixtureWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-wwwroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), ReactShellFixtureHtml);
        return root;
    }

    public bool SkipDefaultBaseUrlOverride { get; init; }

    public Action<IServiceCollection>? ConfigureAdditionalTestServices { get; init; }

    private static bool RunLocalIntegration =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public new async ValueTask DisposeAsync()
    {
        if (_fakeOllamaServer is not null)
        {
            await _fakeOllamaServer.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync();

        try
        {
            if (Directory.Exists(_fixtureWebRoot))
            {
                Directory.Delete(_fixtureWebRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the fixture web root; ignore.
        }

        GC.SuppressFinalize(this);
    }

    public async Task InitializeAsync()
    {
    }

    public string CreateNodeAccessToken()
    {
        var tokenService = Services.GetRequiredService<INodeTokenService>();
        var user = new NodeUser
        {
            Id = "node-admin-test",
            UserName = "admin@example.test",
            Email = "admin@example.test",
            SetupCompleted = true
        };

        return tokenService.CreateAccessToken(user, [NodeAuthorizationPolicies.AdminRole]).AccessToken;
    }

    public void AddNodeBearerToken(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Authorization = new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, CreateNodeAccessToken());
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
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
        builder.UseEnvironment("Testing");
        // Serve the SPA fallback (index.html) from the fixture web root so route-coexistence tests are hermetic on a
        // clean checkout where the real wwwroot has no built SPA (see ReactShellFixtureHtml).
        builder.UseWebRoot(_fixtureWebRoot);
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:node-sqlite"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-{Guid.NewGuid():N}.sqlite")}",
                ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray()),
                ["XE_USE_LOCAL_MODEL_PROVIDER"] = "true",
                ["Ollama:ChatModel"] = "qwen3.5:0.8b",
                ["NodeData:Directory"] = Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-nodedata-{Guid.NewGuid():N}")
            });
        });

        _ = builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

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
                services.AddSingleton<IOllamaApiClient>(_ => new OllamaApiClient(_fakeOllamaServer!.BaseAddress));
                services.AddSingleton<ILocalModelProvider, OllamaLocalModelProvider>();
                // The provider resolver's default for unmapped models is now "llamacpp" (the shipped default model is a
                // GGUF), so the resolver ctor validates that a llamacpp provider is registered. Production always
                // registers it (AddLlamaServerLocalModelProvider); the test host strips the real provider set, so add a
                // lightweight llamacpp stub that answers ProviderName only — unit tests route chat to the Ollama model
                // and never actually dispatch to llama.cpp, so its other members stay unimplemented.
                services.AddSingleton<ILocalModelProvider>(_ =>
                {
                    var llamacpp = Substitute.For<ILocalModelProvider>();
                    llamacpp.ProviderName.Returns("llamacpp");
                    return llamacpp;
                });
                // Resolve the IChatClient explicitly from the Ollama provider. GetRequiredService<ILocalModelProvider>()
                // returns the LAST-registered provider (the llamacpp stub above), whose unconfigured CreateChatClient
                // would hand back an NSubstitute default — so select the Ollama provider by name to guarantee the test
                // chat client is the real Ollama-backed one the unit tests intend.
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
                    // Return a real (but un-routed) HttpClient for ANY named client so DI factories that construct an
                    // HttpClient at resolve time (e.g. the Hugging Face discovery/download clients reached by the
                    // model-fit advisor endpoints) can be built. No real request is made in unit tests — the consuming
                    // endpoints catch transport failures and degrade — so this never performs network I/O.
                    var factory = Substitute.For<IHttpClientFactory>();
                    factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
                    return factory;
                });
            }

            ConfigureAdditionalTestServices?.Invoke(services);
        });

        base.ConfigureWebHost(builder);
    }
}
