namespace XE_Local_AI_Engine.Tests;

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
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public class TestingWebAppFactory : WebApplicationFactory<Program>, IAsyncInitializer, IAsyncDisposable
{
    public bool SkipDefaultBaseUrlOverride { get; init; }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    public async Task InitializeAsync()
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:node-sqlite"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-{Guid.NewGuid():N}.sqlite")}",
                ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray())
            });
        });

        _ = builder.ConfigureTestServices(services =>
        {
            var runLocalIntegration = string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_INTEGRATION"),
                "true",
                StringComparison.OrdinalIgnoreCase);

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

            if (!runLocalIntegration)
            {
                services.RemoveAll<IOllamaApiClient>();
                services.RemoveAll<IChatClient>();
                services.AddSingleton<OllamaApiClient>(_ =>
                {
                    var handler = new MockOllamaHttpHandler();
                    handler.SetModelsResponse();

                    return new OllamaApiClient(new HttpClient(handler)
                    {
                        BaseAddress = new Uri("http://fake-ollama/")
                    });
                });
                services.AddSingleton<IOllamaApiClient>(sp => sp.GetRequiredService<OllamaApiClient>());
                services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<OllamaApiClient>());

                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(_ => Substitute.For<IHttpClientFactory>());
            }
        });

        base.ConfigureWebHost(builder);
    }
}
