namespace XE_Local_AI_Engine.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using OllamaSharp;
using TUnit.Core.Interfaces;
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

            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(_ =>
            {
                var handler = new MockOllamaHttpHandler();
                handler.SetModelsResponse();

                return new OllamaApiClient(new HttpClient(handler)
                {
                    BaseAddress = new Uri("http://fake-ollama/")
                });
            });

            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(_ => Substitute.For<IHttpClientFactory>());
        });

        base.ConfigureWebHost(builder);
    }
}
