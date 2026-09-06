namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Endpoints.Integrations.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one host in this project that actually runs the integration coordinator.
///     <para>
///         <see cref="TestServerWebAppFactory" /> strips every <c>IHostedService</c>, so without this fixture an
///         admitted execution sits <c>Accepted</c> for ever and nothing downstream of the accept path is reachable over
///         HTTP at all. The model seams are substituted because this host resolves no local chat model; the runner
///         substitute raises the SAME dispatcher events the real runner raises, and those events are the whole of what
///         the mapper, the ring and the writer consume.
///     </para>
/// </summary>
public sealed class IntegrationCoordinatorHostFixture : IAsyncInitializer, IAsyncDisposable
{
    private const string LocalModel = "test-local-model";

    /// <summary>The node's invocation lease, which this host does not model: one shared no-op, so nothing to dispose.</summary>
    private static readonly IAsyncDisposable Lease = new NoOpLease();

    public TestServerWebAppFactory Factory { get; } = new()
    {
        ConfigureAdditionalTestServices = Configure
    };

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();

    private static void Configure(IServiceCollection services)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        _ = dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(Lease));

        var runner = Substitute.For<IInvocationRunner>();
        runner.When(static candidate => candidate.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>()))
              .Do(callInfo => RaiseTurn(dispatcher, callInfo.Arg<InvocationExecutionContext>().Package.InvocationId));

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
        services.RemoveAll<IInvocationRunner>();
        services.AddSingleton(runner);
        services.RemoveAll<ILocalDefaultChatModelResolver>();
        services.AddSingleton(localDefault);
        services.RemoveAll<IModelCapabilityResolver>();
        services.AddSingleton(capability);
        services.RemoveAll<ICapacityService>();
        services.AddSingleton(capacity);

        services.AddHostedService<IntegrationExecutionCoordinator>();
    }

    /// <summary>Two content snapshots and a terminal, raised synchronously from inside the run exactly as the real runner does.</summary>
    private static void RaiseTurn(IWorkerEventDispatcher dispatcher, Guid invocationId)
    {
        RaiseState(dispatcher, invocationId, "Two, ", InvocationStatus.Running);
        RaiseState(dispatcher, invocationId, "Two, three, five.", InvocationStatus.Completed);
    }

    private static void RaiseState(IWorkerEventDispatcher dispatcher, Guid invocationId, string content, InvocationStatus status) =>
        dispatcher.InvocationStateChanged += Raise.EventWith(new InvocationStateChangedEventArgs(new InvocationState
        {
            InvocationId = invocationId,
            Status = status,
            StreamedContent = content,
            ModelUsed = LocalModel,
            StartedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch,
            GenerationDurationMs = 3
        }));

    private sealed class NoOpLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}

/// <summary>
///     One real invocation, end to end, over the wire an integrator reads: the accept path, the hosted coordinator, the
///     mapper on the coordinator's own subscription, the ring and the SSE writer, all reached through HTTP. This is the
///     only place the whole chain's frame ORDER is asserted; every other suite tests one link of it.
/// </summary>
[NotInParallel("IntegrationCoordinatorHost")]
public sealed class IntegrationSseEndToEndTests
{
    private const string EventStream = "text/event-stream";

    [ClassDataSource<IntegrationCoordinatorHostFixture>(Shared = SharedType.PerClass)]
    public required IntegrationCoordinatorHostFixture Host { get; init; }

    private TestServerWebAppFactory Factory => Host.Factory;

    /// <summary>Test 39 — the frame sequence of a whole run.</summary>
    [Test]
    public async Task Invoke_WithAStreamAccept_EmitsTheWholeRunInOrderWithAscendingIds()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "e2e-stream");

        var (_, frames) = await StreamAsync(client, seeded, frameLimit: int.MaxValue);

        var order = frames.Select(static frame => frame.Type).ToArray();
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionAccepted, order[0]);
        AssertEx.Equal(expected: 1L, frames[0].Sequence, "The accepted event is always sequence 1.");
        AssertEx.Contains(order, IntegrationStreamEventTypes.ExecutionStarted);
        AssertEx.Contains(order, IntegrationStreamEventTypes.AssistantDelta);
        AssertEx.Contains(order, IntegrationStreamEventTypes.AssistantCompleted);
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCompleted, order[^1], $"The stream must end on the terminal event; it ended on '{order[^1]}'.");
        AssertEx.True(frames.Zip(frames.Skip(count: 1)).All(static pair => pair.Second.Sequence > pair.First.Sequence),
            $"Ids must ascend: [{string.Join(", ", frames.Select(static frame => $"{frame.Sequence}:{frame.Type}"))}]");

        // execution.queued is legitimately absent on an idle node — Accepted -> Running is a legal edge — so its
        // presence is never asserted, only that it cannot appear twice.
        AssertEx.Empty(order.Where(static type => type == IntegrationStreamEventTypes.ExecutionQueued).Skip(count: 1));
    }

    /// <summary>Test 40 — a dropped stream resumed with <c>Last-Event-ID</c> loses nothing and repeats nothing.</summary>
    [Test]
    public async Task Events_ResumedAfterADrop_ConcatenateIntoTheSingleStreamSequence()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "e2e-resume");

        // Two frames, then the response is dropped: what a caller pressing Ctrl-C does.
        var (executionId, first) = await StreamAsync(client, seeded, frameLimit: 2);
        var rest = await ResumeAsync(client, seeded.Key, executionId, first[^1].Sequence);

        var combined = first.Concat(rest).ToArray();
        AssertEx.Equal(expected: 1L, combined[0].Sequence);
        AssertEx.True(combined.Zip(combined.Skip(count: 1)).All(static pair => pair.Second.Sequence > pair.First.Sequence),
            $"A resume must neither repeat nor skip: [{string.Join(", ", combined.Select(static frame => frame.Sequence))}]");
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCompleted, combined[^1].Type, "The resumed half still carries the run to its terminal event.");
    }

    /// <summary>Test 41 — the persisted rows a completed run leaves, and what is deliberately not among them.</summary>
    [Test]
    public async Task Invoke_LeavesThePersistedSubsetAndNoAssistantRows()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "e2e-persisted");

        var (executionId, _) = await StreamAsync(client, seeded, frameLimit: int.MaxValue);

        using var scope = Factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>()
                              .ListEventsAsync(executionId, sinceSequence: 0, limit: 500);

        AssertEx.NotEmpty(rows);
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionAccepted, rows[0].EventType);
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCompleted,
            rows[^1].EventType,
            "The terminal row is the last one, because the drain is awaited before it is written.");
        AssertEx.Empty(rows.Where(static row => row.EventType.StartsWith("assistant.", StringComparison.Ordinal)),
            "Per-token deltas are stream-only, and the final text lives on the owned conversation as an assistant message.");
        AssertEx.True(rows.Select(static row => row.Sequence).SequenceEqual(rows.Select(static row => row.Sequence).Order()),
            "Rows ascend by sequence, though not necessarily contiguously.");
    }

    private async Task<Seeded> SeedAsync(HttpClient client, string prefix)
    {
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, $"{prefix}-agent");
        var trigger = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, prefix, agentId);
        var key = await IntegrationEndpointPayloads.GenerateKeyAsync(Factory, client, $"{prefix}-key");
        return new Seeded(trigger.Name, key.Key);
    }

    private static async Task<(Guid ExecutionId, IReadOnlyList<Frame> Frames)> StreamAsync(HttpClient client, Seeded seeded, int frameLimit)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, IntegrationApiRoutes.Invoke(seeded.TriggerName));
        request.Headers.Add("Authorization", $"Bearer {seeded.Key}");
        request.Headers.Add("Accept", EventStream);
        request.Content = JsonContent.Create(new
        {
            requestId = Guid.NewGuid(),
            inputs = new[]
            {
                new
                {
                    type = "text",
                    text = "Name three primes."
                }
            }
        });

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(EventStream, response.Content.Headers.ContentType?.MediaType);
        var frames = await ReadFramesAsync(response, frameLimit);
        AssertEx.NotEmpty(frames);
        return (frames[0].ExecutionId, frames);
    }

    private static async Task<IReadOnlyList<Frame>> ResumeAsync(HttpClient client, string key, Guid executionId, long lastEventId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, IntegrationApiRoutes.Events(executionId));
        request.Headers.Add("Authorization", $"Bearer {key}");
        request.Headers.Add("Accept", EventStream);
        request.Headers.Add("Last-Event-ID", lastEventId.ToString(CultureInfo.InvariantCulture));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "The ring still holds this run, so the re-attach streams rather than answering 410.");
        return await ReadFramesAsync(response, int.MaxValue);
    }

    private static async Task<IReadOnlyList<Frame>> ReadFramesAsync(HttpResponseMessage response, int frameLimit)
    {
        // A ceiling on the whole read: a stream that never terminalizes must fail the run, not hang it.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var frames = new List<Frame>();
        string? type = null;
        var executionId = Guid.Empty;
        while (frames.Count < frameLimit && await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(line[6..]);
                executionId = document.RootElement.GetProperty("executionId").GetGuid();
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal) && type is not null)
            {
                frames.Add(new Frame(type, long.Parse(line[4..], CultureInfo.InvariantCulture), executionId));
                type = null;
            }
        }

        return frames;
    }

    private sealed record Seeded(string TriggerName, string Key);

    private sealed record Frame(string Type, long Sequence, Guid ExecutionId);
}
