namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The running-models list degrades to an empty 200 so the running panel can keep polling a wedged or restarting
///     llama-server. That degradation used to be a bare <c>catch (Exception)</c>, so a defect anywhere in the endpoint
///     was reported to the operator — and to the eject/update gates that read this list — as "nothing is running".
///     These tests pin both halves: a probe/transport failure still degrades, anything else stays a 500.
/// </summary>
public sealed class ListRunningModelsEndpointTests
{
    private const string RunningRoute = "/api/local/v1/model-fit/running";

    [Test]
    public async Task ListRunning_WhenTheLivenessProbeFails_DegradesToAnEmptyList()
    {
        using var response = await ListWithFailingSupervisorAsync(new HttpRequestException("connection refused")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ListRunningModelsResponse>().ConfigureAwait(false);
        AssertEx.NotNull(body);
        AssertEx.Empty(body!.Items);
    }

    [Test]
    public async Task ListRunning_WhenTheProcessHandleCannotBeQueried_DegradesToAnEmptyList()
    {
        // Process.HasExited on a handle the supervisor no longer owns — the real, expected supervisor failure.
        using var response = await ListWithFailingSupervisorAsync(new InvalidOperationException("No process is associated with this object."))
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task ListRunning_WhenTheSupervisorFailsUnexpectedly_StaysAServerError()
    {
        using var response = await ListWithFailingSupervisorAsync(new KeyNotFoundException("a defect in the snapshot mapper")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> ListWithFailingSupervisorAsync(Exception failure)
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.CheckHealthAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromException<IReadOnlyList<LlamaServerProcessHealth>>(failure));

        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILlamaServerProcessSupervisor>();
                services.AddSingleton(supervisor);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, RunningRoute);
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}
