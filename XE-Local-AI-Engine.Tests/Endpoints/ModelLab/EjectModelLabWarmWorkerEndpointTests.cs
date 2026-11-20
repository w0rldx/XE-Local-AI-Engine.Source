namespace XE_Local_AI_Engine.Tests.Endpoints.ModelLab;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary><c>POST model-lab/warm/eject</c>: 204 when the source evicts, the 409 <c>Busy</c> or <c>OrphanWorker</c>, and the operator policy.</summary>
[Category(TestCategories.Integration)]
public sealed class EjectModelLabWarmWorkerEndpointTests
{
    private const string EjectRoute = "/api/local/v1/model-lab/warm/eject";

    [Test]
    public async Task Eject_OfAnIdleWorker_Is204AndEvictsIt()
    {
        var warm = new FakeModelLabWarmResidentSource
        {
            Resident = new ModelLabWarmResident
            {
                State = RuntimeResidentState.Idle,
                ModelId = "Qwen/Qwen3-0.6B"
            }
        };

        var reply = await PostAsync(warm, static factory => factory.AddNodeBearerToken);

        AssertEx.Equal(HttpStatusCode.NoContent, reply.Status);
        AssertEx.Equal(expected: 1, warm.EjectCalls);
        AssertEx.Null(warm.Resident);
    }

    [Test]
    public async Task Eject_WhileAJobHoldsTheWorker_Is409Busy()
    {
        var warm = new FakeModelLabWarmResidentSource
        {
            EjectResult = ModelLabEjectOutcome.Busy
        };

        var reply = await PostAsync(warm, static factory => factory.AddNodeBearerToken);

        AssertEx.Equal(HttpStatusCode.Conflict, reply.Status);
        using var document = JsonDocument.Parse(reply.Body);
        AssertEx.Equal(nameof(ModelLabStartRefusal.Busy), document.RootElement.GetProperty("reason").GetString());
        AssertEx.Equal(expected: 1, warm.EjectCalls);
    }

    [Test]
    public async Task Eject_WhoseExitWasNotSeen_Is409OrphanWorker_SayingStartsAreRefused()
    {
        var warm = new FakeModelLabWarmResidentSource
        {
            EjectResult = ModelLabEjectOutcome.NotConfirmed
        };

        var reply = await PostAsync(warm, static factory => factory.AddNodeBearerToken);

        AssertEx.Equal(HttpStatusCode.Conflict, reply.Status);
        using var document = JsonDocument.Parse(reply.Body);
        AssertEx.Equal(nameof(ModelLabStartRefusal.OrphanWorker), document.RootElement.GetProperty("reason").GetString());
        AssertEx.Contains(document.RootElement.GetProperty("message").GetString()!, "starts are refused", StringComparison.Ordinal);
    }

    [Test]
    public async Task Eject_WithANonOperatorToken_IsForbiddenBeforeTheSource()
    {
        var warm = new FakeModelLabWarmResidentSource();

        var reply = await PostAsync(warm, static factory => factory.AddNonOperatorBearerToken);

        AssertEx.Equal(HttpStatusCode.Forbidden, reply.Status);
        AssertEx.Equal(expected: 0, warm.EjectCalls);
    }

    private static async Task<EjectReply> PostAsync(FakeModelLabWarmResidentSource warm,
        Func<TestServerWebAppFactory, Action<HttpRequestMessage>> authorize)
    {
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IModelLabWarmResidentSource>();
                services.AddSingleton<IModelLabWarmResidentSource>(warm);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, EjectRoute);
        request.Headers.Add("Origin", "http://localhost");
        authorize(factory)(request);

        using var response = await client.SendAsync(request);
        return new EjectReply
        {
            Status = response.StatusCode,
            Body = await response.Content.ReadAsStringAsync()
        };
    }

    private sealed class EjectReply
    {
        public required HttpStatusCode Status { get; init; }

        public required string Body { get; init; }
    }
}
