namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Tools;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Endpoints.Integrations.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A host that actually runs the integration coordinator AND the real <c>emit_output</c> handler.
///     <para>
///         The model seams are substituted for the reason S2's fixture records: this host resolves no local chat model,
///         so there is no runner to reach. What is NOT substituted is everything this slice owns — the accept path, the
///         session gate, the context builder, the prior-outputs replay, the tool handler, the stores, the ring and the
///         SSE writer. The substituted runner does the two things a real one does that they depend on: it seeds the
///         ambient conversation scope, and it invokes the registered tool.
///     </para>
/// </summary>
public sealed class IntegrationSessionHostFixture : IAsyncInitializer, IAsyncDisposable
{
    private const string LocalModel = "test-local-model";

    private static readonly IAsyncDisposable Lease = new NoOpLease();

    public IntegrationSessionHostFixture() =>
        Factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = Configure
        };

    public TestServerWebAppFactory Factory { get; }

    /// <summary>The runtime context of every turn this host ran, in order, so a suite can assert what turn two carried.</summary>
    public List<IReadOnlyList<ConversationMessageDto>> CapturedContexts { get; } = [];

    /// <summary>The tools each turn's package carried, so the union is observable end to end.</summary>
    public List<IReadOnlyList<string>> CapturedToolNames { get; } = [];

    /// <summary>
    ///     What the model "calls" <c>emit_output</c> with on the NEXT turn: the raw tool arguments, or null to run a
    ///     turn that only answers in prose. Consumed once, exactly as a one-shot tool script would be.
    /// </summary>
    public string? NextEmitOutputArguments { get; set; }

    /// <summary>The answer the substituted runner reports as the turn's final assistant text.</summary>
    public string NextAnswer { get; set; } = "Done.";

    /// <summary>Whatever the tool handed back to the model, for the acknowledgement assertions.</summary>
    public List<string> ToolResults { get; } = [];

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();

    public void Reset()
    {
        CapturedContexts.Clear();
        CapturedToolNames.Clear();
        ToolResults.Clear();
        NextEmitOutputArguments = null;
        NextAnswer = "Done.";
    }

    private void Configure(IServiceCollection services)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        _ = dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(Lease));

        var localDefault = Substitute.For<ILocalDefaultChatModelResolver>();
        _ = localDefault.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(LocalModel);

        var capability = Substitute.For<IModelCapabilityResolver>();
        _ = capability.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                      .Returns(new ModelCapabilitySnapshot(SupportsThinking: false, SupportsTools: true, IsCloud: false));

        var capacity = Substitute.For<ICapacityService>();
        _ = capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                    .Returns(new CapacityDecision(CapacityVerdict.Allow, "Capacity available.", OllamaEvictionWarning: false, Reservation: null));

        services.RemoveAll<IWorkerEventDispatcher>();
        services.AddSingleton(dispatcher);
        services.RemoveAll<ILocalDefaultChatModelResolver>();
        services.AddSingleton(localDefault);
        services.RemoveAll<IModelCapabilityResolver>();
        services.AddSingleton(capability);
        services.RemoveAll<ICapacityService>();
        services.AddSingleton(capacity);

        services.RemoveAll<IInvocationRunner>();
        services.AddSingleton<IInvocationRunner>(provider => BuildRunner(provider, dispatcher));

        services.AddHostedService<IntegrationExecutionCoordinator>();
    }

    private IInvocationRunner BuildRunner(IServiceProvider provider, IWorkerEventDispatcher dispatcher)
    {
        var runner = Substitute.For<IInvocationRunner>();
        runner.When(static candidate => candidate.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>()))
              .Do(callInfo => RunTurn(provider, dispatcher, callInfo.Arg<InvocationExecutionContext>().Package));
        return runner;
    }

    private void RunTurn(IServiceProvider provider, IWorkerEventDispatcher dispatcher, RuntimePackage package)
    {
        CapturedContexts.Add(package.ConversationContext);
        CapturedToolNames.Add([.. package.AllowedTools.Select(static tool => tool.Name)]);

        // The two things the REAL runner does that this slice depends on: the ambient conversation scope for the whole
        // tool loop, and calling the registered handler.
        using (AgentRunConversationContext.BeginScope(package.ConversationId))
        {
            if (NextEmitOutputArguments is { } arguments)
            {
                NextEmitOutputArguments = null;
                var handler = provider.GetServices<IClientLocalToolHandler>()
                                      .Single(candidate => string.Equals(candidate.ToolName, EmitOutputToolDefinition.ToolName, StringComparison.Ordinal));
                ToolResults.Add(handler.ExecuteAsync(arguments).GetAwaiter().GetResult());
            }
        }

        dispatcher.InvocationStateChanged += Raise.EventWith(new InvocationStateChangedEventArgs(new InvocationState
        {
            InvocationId = package.InvocationId,
            Status = InvocationStatus.Completed,
            StreamedContent = NextAnswer,
            ModelUsed = LocalModel,
            StartedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch,
            GenerationDurationMs = 3
        }));
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}

/// <summary>
///     Caller-managed sessions and <c>emit_output</c> over the wire an integrator actually uses: two invocations on one
///     session, the framed replay of what the first one delivered, the <c>external.output</c> frame on the stream, and
///     the two things an operator sees afterwards — the session is not in the chat list, and deleting it takes its
///     executions with it.
/// </summary>
[NotInParallel("IntegrationSessionHost")]
public sealed class IntegrationSessionEndToEndTests
{
    [ClassDataSource<IntegrationSessionHostFixture>(Shared = SharedType.PerClass)]
    public required IntegrationSessionHostFixture Host { get; init; }

    private TestServerWebAppFactory Factory => Host.Factory;

    [Test]
    public async Task TwoInvocationsOnOneSession_TheSecondTurnSeesTheFirstAnswer()
    {
        Host.Reset();
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "e2e-session");

        Host.NextAnswer = "Twenty-one degrees.";
        var first = await InvokeAsync(client, seeded, "What temperature did I send?", sessionId: null);
        await WaitUntilTerminalAsync(client, seeded.Key, first.ExecutionId);

        var second = await InvokeAsync(client, seeded, "And what did you answer?", first.SessionId);
        await WaitUntilTerminalAsync(client, seeded.Key, second.ExecutionId);

        AssertEx.Equal(first.SessionId, second.SessionId, "A caller-managed session is continued, not replaced.");
        AssertEx.Equal(expected: 2, Host.CapturedContexts.Count);

        var turnTwo = Host.CapturedContexts[1];
        AssertEx.Contains(turnTwo.Select(static message => message.Content), "Twenty-one degrees.");
        AssertEx.Contains(turnTwo.Where(static message => message.Role == MessageRole.Assistant).Select(static message => message.Content), "Twenty-one degrees.");
        AssertEx.Equal(expected: 1,
            turnTwo.Count(static message => message.Content.Contains("And what did you answer?", StringComparison.Ordinal)),
            "The seed is sent ONCE — the accept path persisted it before the coordinator read the conversation.");
        AssertEx.Equal("And what did you answer?", turnTwo[^1].Content, "The current turn is last.");
    }

    [Test]
    public async Task EveryIntegrationPackageCarriesEmitOutput()
    {
        Host.Reset();
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "e2e-offer");

        var accepted = await InvokeAsync(client, seeded, "Do the thing.", sessionId: null);
        await WaitUntilTerminalAsync(client, seeded.Key, accepted.ExecutionId);

        AssertEx.Contains(Host.CapturedToolNames[0], EmitOutputToolDefinition.ToolName,
            "The agent lists no tools at all; the coordinator's union is the only way this reaches the package.");
    }

    [Test]
    public async Task AnEmitOutputCall_ProducesAnExternalOutputFrameAndCountsOnTheExecution()
    {
        Host.Reset();
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "e2e-output");
        Host.NextEmitOutputArguments = """{"contentType":"application/json","payload":{"door":"opened"}}""";

        var (executionId, sessionId, frames) = await StreamAsync(client, seeded, "Open the door.", sessionId: null);

        var output = AssertEx.NotNull(frames.SingleOrDefault(static frame => frame.Type == IntegrationStreamEventTypes.ExternalOutput),
            $"Expected one external.output frame; saw [{string.Join(", ", frames.Select(static frame => frame.Type))}].");
        AssertEx.Equal("application/json", output.ContentType);
        AssertEx.Equal("opened", output.Payload.GetProperty("door").GetString(), "The payload crosses to the caller verbatim.");
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCompleted, frames[^1].Type);
        AssertEx.Contains(Host.ToolResults[0], "Output delivered to the caller");
        AssertEx.False(Host.ToolResults[0].Contains("opened", StringComparison.Ordinal), "The acknowledgement never echoes the payload.");

        var status = await ReadExecutionAsync(client, seeded.Key, executionId);
        AssertEx.Equal(expected: 1, status.OutputCount, "outputCount is the execution row's own transactional counter.");
        AssertEx.NotEqual(Guid.Empty, sessionId);
    }

    [Test]
    public async Task TheSecondTurnReplaysWhatTheFirstOneDelivered()
    {
        Host.Reset();
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "e2e-replay");
        Host.NextEmitOutputArguments = """{"contentType":"application/json","payload":{"door":"opened"}}""";

        var first = await InvokeAsync(client, seeded, "Open the door.", sessionId: null);
        await WaitUntilTerminalAsync(client, seeded.Key, first.ExecutionId);

        var second = await InvokeAsync(client, seeded, "Anything else to do?", first.SessionId);
        await WaitUntilTerminalAsync(client, seeded.Key, second.ExecutionId);

        var leading = Host.CapturedContexts[1][0].Content;
        AssertEx.Contains(leading, IntegrationPriorOutputsComposer.Preamble);
        AssertEx.Contains(leading, UntrustedContentFraming.BeginMarkerPrefix);
        AssertEx.Contains(leading, """{"door":"opened"}""");
        AssertEx.True(leading.IndexOf("""{"door":"opened"}""", StringComparison.Ordinal) > leading.IndexOf(UntrustedContentFraming.BeginMarkerPrefix, StringComparison.Ordinal),
            "What it already delivered is replayed as fenced DATA, never as prose it could act on again.");
    }

    [Test]
    public async Task TheOwnedConversationIsAbsentFromTheChatList()
    {
        Host.Reset();
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "e2e-chatlist");

        var accepted = await InvokeAsync(client, seeded, "Do the thing.", sessionId: null);
        await WaitUntilTerminalAsync(client, seeded.Key, accepted.ExecutionId);

        using var scope = Factory.Services.CreateScope();
        var session = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<IIntegrationSessionStore>().GetByIdAsync(accepted.SessionId));

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory, client, HttpMethod.Get, "/api/local/v1/chat/conversations");
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.False(body.Contains(session.ConversationId.ToString("D"), StringComparison.OrdinalIgnoreCase),
            "An integration session's conversation carries kind = 'integration', so the chat list must not show it.");
    }

    [Test]
    public async Task DeletingTheSessionTakesItsExecutionsAndEventsWithIt()
    {
        Host.Reset();
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "e2e-delete");
        Host.NextEmitOutputArguments = """{"contentType":"application/json","payload":{"a":1}}""";

        var accepted = await InvokeAsync(client, seeded, "Do the thing.", sessionId: null);
        await WaitUntilTerminalAsync(client, seeded.Key, accepted.ExecutionId);

        using var deleted = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Delete,
            $"/api/local/v1/integrations/sessions/{accepted.SessionId:D}");
        AssertEx.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var scope = Factory.Services.CreateScope();
        AssertEx.Null(await scope.ServiceProvider.GetRequiredService<IIntegrationSessionStore>().GetByIdAsync(accepted.SessionId));
        AssertEx.Null(await scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>().GetByIdAsync(accepted.ExecutionId),
            "The conversation purge cascades: a session's executions carry conversation-derived content and go with it.");
        AssertEx.Empty(await scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>()
                                  .ListEventsAsync(accepted.ExecutionId, sinceSequence: 0, limit: 500));
    }

    private async Task<Seeded> SeedAsync(HttpClient client, string prefix)
    {
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, $"{prefix}-agent");
        var trigger = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, prefix, agentId, sessionPolicy: "CallerManaged");
        var key = await IntegrationEndpointPayloads.GenerateKeyAsync(Factory, client, $"{prefix}-key");
        return new Seeded(trigger.Name, key.Key);
    }

    private static async Task<Accepted> InvokeAsync(HttpClient client, Seeded seeded, string text, Guid? sessionId)
    {
        using var request = BuildInvoke(seeded, text, sessionId);
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<AcceptedBody>(IntegrationEndpointPayloads.Json));
        return new Accepted(body.ExecutionId, body.SessionId);
    }

    private static async Task<(Guid ExecutionId, Guid SessionId, IReadOnlyList<Frame> Frames)> StreamAsync(HttpClient client,
        Seeded seeded,
        string text,
        Guid? sessionId)
    {
        using var request = BuildInvoke(seeded, text, sessionId);
        request.Headers.Add("Accept", "text/event-stream");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        // A ceiling on the whole read: a stream that never terminalizes must fail the run, not hang it.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var frames = new List<Frame>();
        string? type = null;
        var executionId = Guid.Empty;
        var owningSession = Guid.Empty;
        string? contentType = null;
        JsonElement payload = default;
        while (await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(line[6..]);
                executionId = document.RootElement.GetProperty("executionId").GetGuid();
                owningSession = document.RootElement.GetProperty("sessionId").GetGuid();
                contentType = document.RootElement.TryGetProperty("contentType", out var declared) && declared.ValueKind == JsonValueKind.String
                    ? declared.GetString()
                    : null;
                payload = document.RootElement.TryGetProperty("payload", out var body) ? body.Clone() : default;
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal) && type is not null)
            {
                frames.Add(new Frame(type, contentType, payload));
                type = null;
            }
        }

        AssertEx.NotEmpty(frames);
        return (executionId, owningSession, frames);
    }

    private static HttpRequestMessage BuildInvoke(Seeded seeded, string text, Guid? sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, IntegrationApiRoutes.Invoke(seeded.TriggerName));
        request.Headers.Add("Authorization", $"Bearer {seeded.Key}");
        request.Content = JsonContent.Create(new
        {
            requestId = Guid.NewGuid(),
            sessionId,
            inputs = new[]
            {
                new
                {
                    type = "text",
                    text
                }
            }
        });
        return request;
    }

    /// <summary>
    ///     Polls the status route until the run is terminal. The coordinator dispatches each execution onto its own
    ///     task, so an assertion made straight after the 202 would race it.
    /// </summary>
    private static async Task WaitUntilTerminalAsync(HttpClient client, string key, Guid executionId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var status = await ReadExecutionAsync(client, key, executionId);
            if (status.Status is "completed" or "failed" or "cancelled")
            {
                AssertEx.Equal("completed", status.Status, $"The run ended '{status.Status}' ({status.FailureCategory}: {status.FailureSummary}).");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
        }
    }

    private static async Task<StatusBody> ReadExecutionAsync(HttpClient client, string key, Guid executionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, IntegrationApiRoutes.Execution(executionId));
        request.Headers.Add("Authorization", $"Bearer {key}");
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return AssertEx.NotNull(await response.Content.ReadFromJsonAsync<StatusBody>(IntegrationEndpointPayloads.Json));
    }

    private sealed record Seeded(string TriggerName, string Key);

    private sealed record Accepted(Guid ExecutionId, Guid SessionId);

    private sealed record AcceptedBody(Guid ExecutionId, Guid SessionId, string Status);

    private sealed record StatusBody(Guid ExecutionId, Guid SessionId, string Status, string? FailureCategory, string? FailureSummary, int OutputCount);

    private sealed record Frame(string Type, string? ContentType, JsonElement Payload);
}
