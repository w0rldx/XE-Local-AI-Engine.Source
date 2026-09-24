namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The running-models family at the HTTP layer. The list degrades to an empty 200 so the running panel can keep
///     polling a wedged or restarting llama-server. That degradation used to be a bare <c>catch (Exception)</c>, so a
///     defect anywhere in the endpoint was reported to the operator — and to the eject/update gates that read this list —
///     as "nothing is running". These tests pin both halves: a probe/transport failure still degrades, anything else
///     stays a 500. The eject tests pin the sibling <c>POST model-fit/running/eject</c> contract: its two refusals, and
///     the outcome projection the running panel and the runtime gates read.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ListRunningModelsEndpointTests
{
    private const string EjectRoute = "/api/local/v1/model-fit/running/eject";

    private const string RunningRoute = "/api/local/v1/model-fit/running";

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ListRunning_WhenTheLivenessProbeFails_DegradesToAnEmptyList()
    {
        using var response = await ListWithFailingSupervisorAsync(new HttpRequestException("connection refused"));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ListRunningModelsResponse>();
        AssertEx.NotNull(body);
        AssertEx.Empty(body!.Items);
    }

    [Test]
    public async Task ListRunning_WhenTheProcessHandleCannotBeQueried_DegradesToAnEmptyList()
    {
        // Process.HasExited on a handle the supervisor no longer owns — the real, expected supervisor failure.
        using var response = await ListWithFailingSupervisorAsync(new InvalidOperationException("No process is associated with this object."));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task ListRunning_WhenTheSupervisorFailsUnexpectedly_StaysAServerError()
    {
        using var response = await ListWithFailingSupervisorAsync(new KeyNotFoundException("a defect in the snapshot mapper"));

        AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Eject_WhenTheModelNameIsBlank_RefusesWithoutTouchingTheSupervisor(string modelName)
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();

        var (status, body) = await EjectAsync(supervisor, new EjectRunningModelRequest
        {
            ModelName = modelName
        });

        AssertEx.Equal(HttpStatusCode.BadRequest, status);
        AssertEx.Contains(body, "A model name is required.");
        await supervisor.DidNotReceiveWithAnyArgs().EjectAsync(default!, default, default, default);
    }

    [Test]
    public async Task Eject_WhenTheRoleIsUnknown_RefusesWithoutTouchingTheSupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();

        var (status, body) = await EjectAsync(supervisor, new EjectRunningModelRequest
        {
            ModelName = "model-a",
            Role = "vision"
        });

        AssertEx.Equal(HttpStatusCode.BadRequest, status);
        AssertEx.Contains(body, "Role is not supported.");
        await supervisor.DidNotReceiveWithAnyArgs().EjectAsync(default!, default, default, default);
    }

    [Test]
    [Arguments(LlamaServerEjectOutcome.Ejected, "ejected")]
    [Arguments(LlamaServerEjectOutcome.TimedOutStillBusy, "timed_out_still_busy")]
    [Arguments(LlamaServerEjectOutcome.ForcedWhileBusy, "forced")]
    [Arguments(LlamaServerEjectOutcome.NotRunning, "not_running")]
    public async Task Eject_ProjectsTheSupervisorOutcomeOntoTheWireContract(LlamaServerEjectOutcome outcome, string expectedOutcome)
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EjectAsync("model-a", ModelRole.Embedding, force: true, Arg.Any<CancellationToken>()).Returns(outcome);

        var (status, body) = await EjectAsync(supervisor,
            new EjectRunningModelRequest
            {
                ModelName = "  model-a  ",
                Role = "EMBEDDING",
                Force = true
            });

        AssertEx.Equal(HttpStatusCode.OK, status);
        var response = AssertEx.NotNull(JsonSerializer.Deserialize<EjectRunningModelResponse>(body, WebJsonOptions));
        AssertEx.Equal("model-a", response.ModelName);
        AssertEx.Equal("embedding", response.Role);
        AssertEx.Equal(expectedOutcome, response.Outcome);
        // The trimmed name, the parsed role and the force flag reach the supervisor — a NotRunning default from an
        // unmatched call would otherwise read as a pass on the not_running row.
        await supervisor.Received(1).EjectAsync("model-a", ModelRole.Embedding, force: true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Eject_WithoutABearerToken_IsRejectedBeforeTheSupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();

        var (status, _) = await EjectAsync(supervisor,
            new EjectRunningModelRequest
            {
                ModelName = "model-a"
            },
            authenticate: false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, status);
        await supervisor.DidNotReceiveWithAnyArgs().EjectAsync(default!, default, default, default);
    }

    private static async Task<(HttpStatusCode Status, string Body)> EjectAsync(ILlamaServerProcessSupervisor supervisor,
        EjectRunningModelRequest body,
        bool authenticate = true)
    {
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILlamaServerProcessSupervisor>();
                services.AddSingleton(supervisor);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, EjectRoute)
        {
            Content = JsonContent.Create(body)
        };
        if (authenticate)
        {
            factory.AddNodeBearerToken(request);
        }

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
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
        return await client.SendAsync(request);
    }
}
