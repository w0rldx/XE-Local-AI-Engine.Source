namespace XE_Local_AI_Engine.Tests.Endpoints.Invocations;

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Contracts.Events;
using XE_Local_AI_Engine.Client.Endpoints.Invocations.V1;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InvocationMonitorEndpointTests
{
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.Parse("2026-05-25T10:00:00Z");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Test]
    public async Task GetInvocations_WhenAuthorized_ReturnsCurrentAndHistory()
    {
        var currentInvocationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var historyInvocationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.CurrentInvocation.Returns(new InvocationState
        {
            InvocationId = currentInvocationId,
            ConversationId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Status = InvocationStatus.Running,
            ModelUsed = "qwen3:8b",
            StartedAt = FrozenNow.AddSeconds(-10),
            LastUpdatedAt = FrozenNow,
            StreamedChunkCount = 2,
            StreamedThinkingChunkCount = 1,
            PendingApproval = new InvocationApprovalState("approval-1", "Approve tool call", FrozenNow)
        });
        var history = Substitute.For<IInvocationHistory>();
        history.Capacity.Returns(50);
        history.Snapshot().Returns([
            new InvocationHistoryEntry(historyInvocationId,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                InvocationStatus.Failed,
                "qwen3:0.6b",
                FrozenNow.AddMinutes(-5),
                FrozenNow.AddMinutes(-4),
                "Bearer super-secret-token api_key=abc123",
                FailureCategory.AgentRuntime,
                StreamedChunkCount: 3,
                StreamedThinkingChunkCount: 2)
        ]);
        await using var factory = CreateFactory(dispatcher, history);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/invocations");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var monitor = Deserialize<InvocationMonitorResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 50, monitor.HistoryCapacity);
        var current = AssertEx.NotNull(monitor.Current);
        AssertEx.Equal(currentInvocationId, current.InvocationId);
        AssertEx.Equal(InvocationStatus.Running, current.Status);
        AssertEx.True(current.HasPendingApproval);
        AssertEx.False(current.HasPendingQuestion);
        AssertEx.ContainsSingle(monitor.History, entry => entry.InvocationId == historyInvocationId
                                                          && entry.Status == InvocationStatus.Failed
                                                          && entry.Error == "Invocation ended with a failure. See local logs for details."
                                                          && entry.DurationMs == 60_000);
        AssertEx.False(body.Contains("super-secret-token", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(body.Contains("api_key", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(body.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    // A turn parked on an ask_user question is otherwise indistinguishable from an ordinary running one on this page —
    // no output chunks, no pending approval, nothing to explain the silence. The flag is content-free by design: the
    // question text is operator/model content and must not reach an ops endpoint, so only the boolean is asserted here.
    [Test]
    public async Task GetInvocations_WhenParkedOnQuestion_ReportsPendingQuestionWithoutItsContent()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.CurrentInvocation.Returns(new InvocationState
        {
            InvocationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ConversationId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Status = InvocationStatus.Running,
            StartedAt = FrozenNow.AddSeconds(-10),
            LastUpdatedAt = FrozenNow,
            PendingQuestion = new InvocationUserQuestionState("question-1",
                "call-1",
                "ask_user",
                [
                    new UserQuestionSpec("Auth", "Which auth method?", MultiSelect: false, [
                        new UserQuestionOption("OAuth device flow", Description: null, Recommended: true),
                        new UserQuestionOption("Personal access token", Description: null, Recommended: false)
                    ])
                ],
                FrozenNow)
        });
        var history = Substitute.For<IInvocationHistory>();
        history.Capacity.Returns(50);
        history.Snapshot().Returns([]);
        await using var factory = CreateFactory(dispatcher, history);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/invocations");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var monitor = Deserialize<InvocationMonitorResponse>(body);

        var current = AssertEx.NotNull(monitor.Current);
        AssertEx.True(current.HasPendingQuestion);
        AssertEx.False(current.HasPendingApproval);
        AssertEx.False(body.Contains("Which auth method?", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(body.Contains("OAuth device flow", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task GetInvocations_WhenIdle_ReturnsEmptyCurrentAndHistory()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.CurrentInvocation.Returns((InvocationState?)null);
        var history = Substitute.For<IInvocationHistory>();
        history.Capacity.Returns(50);
        history.Snapshot().Returns([]);
        await using var factory = CreateFactory(dispatcher, history);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/invocations");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var monitor = await ReadJsonAsync<InvocationMonitorResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Null(monitor.Current);
        AssertEx.Empty(monitor.History);
    }

    [Test]
    public async Task GetInvocations_WhenMissingBearerToken_ReturnsUnauthorized()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var history = Substitute.For<IInvocationHistory>();
        await using var factory = CreateFactory(dispatcher, history);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/local/v1/invocations").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        history.DidNotReceive().Snapshot();
    }

    private static TestingWebAppFactory CreateFactory(IWorkerEventDispatcher dispatcher, IInvocationHistory history)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWorkerEventDispatcher>();
                services.RemoveAll<IInvocationHistory>();
                services.AddSingleton(dispatcher);
                services.AddSingleton(history);
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(json, JsonOptions));
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return Deserialize<T>(body);
    }
}
