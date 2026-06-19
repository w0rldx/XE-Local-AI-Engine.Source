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
using XE_Local_AI_Engine.Providers.Ollama;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public class TestingWebAppFactory : WebApplicationFactory<Program>, IAsyncInitializer, IAsyncDisposable
{
    private static readonly SemaphoreSlim HostStartupLock = new(1, 1);

    private readonly FakeOllamaServer? _fakeOllamaServer;

    public TestingWebAppFactory(FakeOllamaOptions? fakeOllamaOptions = null)
    {
        if (!RunLocalIntegration)
        {
            _fakeOllamaServer = FakeOllamaServer.StartAsync(fakeOllamaOptions ?? new FakeOllamaOptions
            {
                Models = ["qwen3.5:0.8b", "qwen3-embedding:0.6b"]
            }).GetAwaiter().GetResult();
        }
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
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:node-sqlite"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-{Guid.NewGuid():N}.sqlite")}",
                ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray()),
                ["XE_USE_LOCAL_MODEL_PROVIDER"] = "true",
                ["Ollama:ChatModel"] = "qwen3.5:0.8b"
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
                services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<ILocalModelProvider>().CreateChatClient(new LocalModelSelection
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
