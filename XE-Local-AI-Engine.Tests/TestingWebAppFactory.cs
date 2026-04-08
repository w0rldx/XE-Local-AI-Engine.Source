namespace XE_Local_AI_Engine.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using TUnit.Core.Interfaces;

public class TestingWebAppFactory : WebApplicationFactory<Program>, IAsyncInitializer, IAsyncDisposable
{
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
        });

        base.ConfigureWebHost(builder);
    }
}
