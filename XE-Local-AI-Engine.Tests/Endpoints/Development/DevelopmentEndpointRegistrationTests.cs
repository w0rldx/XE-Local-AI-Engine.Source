namespace XE_Local_AI_Engine.Tests.Endpoints.Development;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Development;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the marker split: the FastEndpoints registration filter drops every <see cref="IDevelopmentEndpoint" />
///     when Development Mode is off, while the capability endpoint stays unmarked so it can report the disabled state.
/// </summary>
/// <remarks>Their services are not registered then, and FastEndpoints activates every endpoint at startup.</remarks>
[Category(TestCategories.Unit)]
public sealed class DevelopmentEndpointRegistrationTests
{
    [Test]
    public void EveryDevelopmentEndpointExceptCapabilityCarriesTheRegistrationMarker()
    {
        var endpoints = typeof(GetDevelopmentCapabilityEndpoint).Assembly
                                                                .GetTypes()
                                                                .Where(static type => type is { IsAbstract: false, IsPublic: true }
                                                                                      && type.Namespace == typeof(GetDevelopmentCapabilityEndpoint).Namespace
                                                                                      && typeof(IEndpoint).IsAssignableFrom(type))
                                                                .ToArray();

        AssertEx.True(endpoints.Length > 1);
        foreach (var endpoint in endpoints)
        {
            var expected = endpoint != typeof(GetDevelopmentCapabilityEndpoint);
            AssertEx.Equal(expected, typeof(IDevelopmentEndpoint).IsAssignableFrom(endpoint));
        }
    }
}
