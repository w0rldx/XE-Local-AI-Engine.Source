namespace XE_Local_AI_Engine.Tests.Integration;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The real host, once its lifetime says stopping, refuses a hub negotiate before authentication or routing can see
///     it, so no late hub connection holds the shutdown.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ShutdownRequestGuardIntegrationTests
{
    [Test]
    public async Task HubNegotiate_AfterApplicationStopping_Gets503()
    {
        // Its own host: stopping the lifetime is a one-way state change a shared factory could not take back.
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();

        lifetime.StopApplication();
        using var response = await client.PostAsync($"{LocalApiRoutes.LocalChat.Hub}/negotiate?negotiateVersion=1", content: null);

        AssertEx.True(lifetime.ApplicationStopping.IsCancellationRequested);
        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertEx.Equal(ShutdownRequestGuard.RetryAfterSeconds, response.Headers.GetValues("Retry-After").Single());
    }
}
