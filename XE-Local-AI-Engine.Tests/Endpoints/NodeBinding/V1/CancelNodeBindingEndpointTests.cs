namespace XE_Local_AI_Engine.Tests.Endpoints.NodeBinding.V1;

using System.Net;
using System.Net.Http.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>POST binding/cancel</c>: operator-gated, and unconditionally 200 with <c>cancelled=true</c> — cancelling when
///     nothing is in flight is a no-op, not an error, so the UI can always offer the button.
/// </summary>
public sealed class CancelNodeBindingEndpointTests
{
    private const string Route = "/api/local/v1/binding/cancel";

    [Test]
    public async Task Cancel_WhenAnonymous_Returns401()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Cancel_WhenAuthenticatedButNotOperator_Returns403()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();
        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        factory.AddNonOperatorBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Cancel_WhenOperator_Returns200AndReachesTheService()
    {
        await using var bindingService = NodeBindingEndpointHost.CreateService();

        await using var factory = NodeBindingEndpointHost.Create(bindingService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<CancelNodeBindingResponse>().ConfigureAwait(false));
        AssertEx.True(payload.Cancelled, "The cancel endpoint reports the request as accepted.");
        await bindingService.Received(requiredNumberOfCalls: 1).CancelAsync().ConfigureAwait(false);
    }
}
