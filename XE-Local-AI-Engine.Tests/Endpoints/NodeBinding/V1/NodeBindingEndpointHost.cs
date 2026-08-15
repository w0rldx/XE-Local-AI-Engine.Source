namespace XE_Local_AI_Engine.Tests.Endpoints.NodeBinding.V1;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Builds a host whose <see cref="INodeBindingService" /> is a substitute. The real one POSTs to the Central
///     Platform on start/poll, and the test host's <c>IHttpClientFactory</c> hands out a live <c>HttpClient</c> — so
///     without this seam a binding endpoint test would make a real network call to the configured base URL.
/// </summary>
internal static class NodeBindingEndpointHost
{
    public static TestServerWebAppFactory Create(INodeBindingService bindingService)
    {
        ArgumentNullException.ThrowIfNull(bindingService);

        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeBindingService>();
                services.AddSingleton(bindingService);
            }
        };
    }

    public static INodeBindingService CreateService() => Substitute.For<INodeBindingService>();
}
