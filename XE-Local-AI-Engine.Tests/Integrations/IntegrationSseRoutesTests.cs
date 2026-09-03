namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Endpoints.Integrations.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The stream as an integrator reaches it: inside <c>/api/local/v1</c>, on the integration key scheme, with
///     <c>Last-Event-ID</c> resume and a 410 that is a real status on an unstarted response rather than a reset
///     connection.
///     <para>
///         Most tests seed the ring directly through the node's own singleton buffer instead of running a model. That
///         is not a shortcut: the buffer IS the replay authority, and driving it by hand is the only way to place a
///         reader below the floor, above the head or on an untracked execution deterministically.
///     </para>
/// </summary>
public sealed class IntegrationSseRoutesTests
{
    private const string EventStream = "text/event-stream";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    /// <summary>Test 30 — the two shapes of an accepted invoke, chosen by <c>Accept</c>.</summary>
    [Test]
    public async Task Invoke_WithAJsonAccept_Answers202AndWithAStreamAcceptOpensTheStream()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "invoke-accept");

        using var json = await SendAsync(client, HttpMethod.Post, IntegrationApiRoutes.Invoke(seeded.TriggerName), seeded.BroadKey, accept: "application/json");
        AssertEx.Equal(HttpStatusCode.Accepted, json.StatusCode);
        AssertEx.Equal("application/json", json.Content.Headers.ContentType?.MediaType);

        using var streamed = await SendAsync(client,
            HttpMethod.Post,
            IntegrationApiRoutes.Invoke(seeded.TriggerName),
            seeded.BroadKey,
            EventStream,
            HttpCompletionOption.ResponseHeadersRead);

        AssertEx.Equal(HttpStatusCode.OK, streamed.StatusCode);
        AssertEx.Equal(EventStream, streamed.Content.Headers.ContentType?.MediaType);
        var first = await ReadFirstFrameAsync(streamed);
        AssertEx.Contains(first, $"event: {IntegrationStreamEventTypes.ExecutionAccepted}");
        AssertEx.Contains(first, "id: 1", message: "The accepted event is always sequence 1, so a caller can resume from the very first frame.");
    }

    /// <summary>Test 31 — a rejection stays a JSON status even when the caller asked for a stream.</summary>
    [Test]
    public async Task Invoke_WhenTheQueueIsFull_Answers503EvenForAStreamAccept()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "invoke-queue-full");

        // The per-principal admission cap is 2, and every seeded row counts as active.
        for (var index = 0; index < 2; index++)
        {
            _ = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: false, active: true);
        }

        using var response = await SendAsync(client, HttpMethod.Post, IntegrationApiRoutes.Invoke(seeded.TriggerName), seeded.BroadKey, EventStream);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertEx.Equal("5", response.Headers.RetryAfter?.ToString());
        AssertEx.Equal("application/json", response.Content.Headers.ContentType?.MediaType,
            "Every rejection happens before the Accept branch, so it is a real status on an unstarted response.");
    }

    /// <summary>Test 32 — resume.</summary>
    [Test]
    public async Task Events_WithALastEventId_StartsAtTheNextSequence()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-resume");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: true);
        AppendPhases(executionId, count: 4, terminal: true);

        using var response = await SendAsync(client,
            HttpMethod.Get,
            IntegrationApiRoutes.Events(executionId),
            seeded.BroadKey,
            EventStream,
            HttpCompletionOption.ResponseHeadersRead,
            lastEventId: "3");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertEx.True(Sequences(body).SequenceEqual([4L, 5L, 6L]), $"Expected the stream to resume at 4; got [{string.Join(", ", Sequences(body))}].");
    }

    /// <summary>Test 33 — below the retained floor, and the body has to name the way out.</summary>
    [Test]
    public async Task Events_BelowTheRetainedFloor_Answers410NamingBothFallbacks()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-below-floor");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: true);
        AppendPhases(executionId, count: 3, terminal: true);
        TrimToFloor(executionId);

        using var response = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Events(executionId), seeded.BroadKey, EventStream, lastEventId: "0");

        AssertEx.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertEx.Contains(body, $"/api/local/v1/integration-api/executions/{executionId:D}/events");
        AssertEx.Contains(body, $"/api/local/v1/integration-api/executions/{executionId:D}",
            message: "A 410 that does not name the recovery route is a dead end for the caller.");
        AssertEx.False(body.Contains("event:", StringComparison.Ordinal), "The refusal must not be a partially written stream.");
    }

    /// <summary>Test 34 — above the head.</summary>
    [Test]
    public async Task Events_AboveTheHead_Answers410RatherThanAHangingStream()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-above-head");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: true);
        AppendPhases(executionId, count: 2, terminal: true);

        using var response = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Events(executionId), seeded.BroadKey, EventStream, lastEventId: "99");

        AssertEx.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    /// <summary>Test 35 — a row that exists on an execution the ring no longer tracks is 410, never 404.</summary>
    [Test]
    public async Task Events_ForAnExecutionTheRingNoLongerTracks_Answers410NotFound()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-untracked");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: false);

        using var response = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Events(executionId), seeded.BroadKey, EventStream);

        AssertEx.Equal(HttpStatusCode.Gone, response.StatusCode,
            "The row exists and the caller owns it: 404 would say it does not, and IsTracked is what separates the two.");
    }

    /// <summary>Test 36 — masking by principal, and by the CURRENT key's allowlist.</summary>
    [Test]
    public async Task Events_ForAForeignPrincipalOrANarrowKey_Answers404ByteIdenticalToAnUnknownId()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-masking");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: true);
        AppendPhases(executionId, count: 1, terminal: true);

        using var unknown = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Events(Guid.NewGuid()), seeded.BroadKey, EventStream);
        using var foreign = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Events(executionId), seeded.ForeignKey, EventStream);
        using var narrow = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Events(executionId), seeded.NarrowKey, EventStream);
        using var revoked = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Events(executionId), "xeint_notarealkey", EventStream);

        var expected = await unknown.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        AssertEx.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        AssertEx.Equal(HttpStatusCode.NotFound, narrow.StatusCode,
            "A key scoped to one trigger must not read its own principal's executions under another; that is the bypass round 5 closed.");
        AssertEx.Equal(expected, await foreign.Content.ReadAsStringAsync());
        AssertEx.Equal(expected, await narrow.Content.ReadAsStringAsync());
        AssertEx.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode, "An unusable credential is 401 from the handler, never a 403 and never a 404.");
    }

    /// <summary>Test 36a — a second credential of the SAME integrator is not masked out.</summary>
    [Test]
    public async Task Events_ForASecondKeyOfTheSamePrincipal_ReturnsTheCallersOwnStream()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-second-key");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: true);
        AppendPhases(executionId, count: 1, terminal: true);
        var second = await IntegrationEndpointPayloads.GenerateKeyAsync(Factory, client, "events-second-key-rotated", allowedTriggerIds: null, seeded.PrincipalId);

        using var response = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Events(executionId), second.Key, EventStream);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "Ownership is the principal, so a rotated or additional key keeps seeing its integrator's own runs.");
        AssertEx.NotEmpty(Sequences(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>Test 36b — the persisted-rows shape, which is what a 410 sends a caller to.</summary>
    [Test]
    public async Task Events_WithAJsonAccept_ReturnTheCommittedRowsEvenWhenTheRingHasDroppedTheRun()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-json");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: false, active: true);
        for (var sequence = 2; sequence <= 6; sequence++)
        {
            await PersistEventAsync(executionId, sequence, IntegrationStreamEventTypes.ToolStarted, $$"""{"name":"tool{{sequence}}"}""");
        }

        // Not tracked at all, which is the exact state a restart or a TTL sweep leaves behind.
        AssertEx.False(Factory.Services.GetRequiredService<IIntegrationExecutionEventBuffer>().IsTracked(executionId));

        using var page = await SendAsync(client, HttpMethod.Get, $"{IntegrationApiRoutes.Events(executionId)}?sinceSeq=0&limit=5000", seeded.BroadKey, "application/json");

        AssertEx.Equal(HttpStatusCode.OK, page.StatusCode, "The persisted shape reads the database, so it never answers 410.");
        var rows = AssertEx.NotNull(await page.Content.ReadFromJsonAsync<EventBody[]>(IntegrationEndpointPayloads.Json));
        AssertEx.True(rows.Length <= IntegrationEventPage.MaxLimit, "A limit of 5000 is clamped, because a bounded page is the point.");
        AssertEx.True(rows.Select(static row => row.Sequence).SequenceEqual([1L, 2L, 3L, 4L, 5L, 6L]));
        AssertEx.Empty(rows.Where(static row => row.EventType.StartsWith("assistant.", StringComparison.Ordinal)));
        AssertEx.Equal("""{"name":"tool2"}""", rows[1].DetailJson, "detailJson crosses the wire as decrypted text.");

        // Paging is by watermark: hand the last sequence back and get the next page with no repeat.
        using var next = await SendAsync(client, HttpMethod.Get, $"{IntegrationApiRoutes.Events(executionId)}?sinceSeq=3", seeded.BroadKey, "application/json");
        var tail = AssertEx.NotNull(await next.Content.ReadFromJsonAsync<EventBody[]>(IntegrationEndpointPayloads.Json));
        AssertEx.True(tail.Select(static row => row.Sequence).SequenceEqual([4L, 5L, 6L]), "sinceSeq is exclusive.");
    }

    /// <summary>Test 36c — the link that makes the recovery route discoverable without reading the docs.</summary>
    [Test]
    public async Task GetExecution_CarriesTheEventsLink()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-link");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: false);

        using var response = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Execution(executionId), seeded.BroadKey, "application/json");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<StatusLinksBody>(IntegrationEndpointPayloads.Json));
        AssertEx.Equal(IntegrationApiRoutes.Events(executionId), body.Links.Events);
        AssertEx.Equal(IntegrationApiRoutes.Execution(executionId), body.Links.Self);
    }

    /// <summary>Test 36d — a narrow key is masked out of all three external routes, and the key row is re-read per request.</summary>
    [Test]
    public async Task ExternalRoutes_MaskANarrowKeyOnEveryShapeAndRereadTheKeyPerRequest()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-narrow-all");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: true);
        AppendPhases(executionId, count: 1, terminal: true);

        foreach (var (route, accept) in new[]
                 {
                     (IntegrationApiRoutes.Events(executionId), EventStream),
                     (IntegrationApiRoutes.Events(executionId), "application/json"),
                     (IntegrationApiRoutes.Execution(executionId), "application/json")
                 })
        {
            using var masked = await SendAsync(client, HttpMethod.Get, route, seeded.NarrowKey, accept);
            using var broad = await SendAsync(client, HttpMethod.Get, route, seeded.BroadKey, accept);

            AssertEx.Equal(HttpStatusCode.NotFound, masked.StatusCode, $"{route} with accept '{accept}' must be masked for a key the trigger is out of scope for.");
            AssertEx.Equal(HttpStatusCode.OK, broad.StatusCode, $"{route} with accept '{accept}' must still serve the broad key, or this is a blanket refusal rather than scoping.");
        }

        // Narrow the BROAD key and re-attach: the allowlist is not a claim, so it binds on the very next request.
        var narrowed = await IntegrationEndpointPayloads.GenerateKeyAsync(Factory, client, "events-narrow-all-rotated", [Guid.NewGuid()], seeded.PrincipalId);
        using var afterNarrowing = await SendAsync(client, HttpMethod.Get, IntegrationApiRoutes.Events(executionId), narrowed.Key, EventStream, lastEventId: "1");

        AssertEx.Equal(HttpStatusCode.NotFound, afterNarrowing.StatusCode, "The key row is re-read per request, so a narrowed credential loses access at once.");
    }

    /// <summary>Test 37 — the operator's timeline reads the table, not the ring.</summary>
    [Test]
    public async Task AdminEvents_ReturnThePersistedRowsWithDecryptedDetailAndNoAssistantNoise()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "admin-events");
        // Left live on purpose: the closing row a terminalized seed writes would sit above the three this asserts on.
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: false, active: true);
        await PersistEventAsync(executionId, sequence: 2, IntegrationStreamEventTypes.ToolStarted, """{"name":"read_file"}""");
        await PersistEventAsync(executionId, sequence: 3, IntegrationStreamEventTypes.ExecutionCompleted, detailJson: null);

        using var response = await IntegrationEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Get,
            $"/api/local/v1/integrations/executions/{executionId:D}/events");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<EventListBody>(IntegrationEndpointPayloads.Json));
        AssertEx.True(body.Items.Select(static item => item.Sequence).SequenceEqual([1L, 2L, 3L]), "The accepted event is row one, and the page ascends.");
        AssertEx.Equal("""{"name":"read_file"}""", body.Items[1].DetailJson, "The store hands back decrypted text; no consumer ever sees the stored byte[].");
        AssertEx.Empty(body.Items.Where(static item => item.EventType.StartsWith("assistant.", StringComparison.Ordinal)),
            "Per-token deltas are stream-only and the final text lives on the owned conversation.");
    }

    /// <summary>Test 38 — the loopback middleware passes a non-browser caller that sends no Origin.</summary>
    [Test]
    public async Task Events_WithNoOriginHeader_IsNotRefusedByTheLoopbackMiddleware()
    {
        using var client = Factory.CreateClient();
        var seeded = await SeedAsync(client, "events-no-origin");
        var executionId = await SeedExecutionAsync(seeded.TriggerId, seeded.PrincipalId, seeded.KeyPrefix, tracked: true);
        AppendPhases(executionId, count: 1, terminal: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, IntegrationApiRoutes.Events(executionId));
        request.Headers.Add("Authorization", $"Bearer {seeded.BroadKey}");
        request.Headers.Add("Accept", EventStream);
        AssertEx.False(request.Headers.Contains("Origin"));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode,
            "A curl or a webhook sender has no Origin; refusing that would make the whole external family unreachable. TestServer proves little here — the live curl round is the real evidence.");
    }

    private void AppendPhases(Guid executionId, int count, bool terminal)
    {
        var buffer = Factory.Services.GetRequiredService<IIntegrationExecutionEventBuffer>();
        for (var index = 0; index < count; index++)
        {
            _ = buffer.Append(executionId, Guid.NewGuid(), IntegrationStreamEventTypes.ExecutionStarted, contentType: null, payload: null);
        }

        if (terminal)
        {
            _ = buffer.Append(executionId, Guid.NewGuid(), IntegrationStreamEventTypes.ExecutionCompleted, contentType: null, payload: null);
        }
    }

    /// <summary>
    ///     Moves the floor by appending past the ring's byte cap. The node's configured capacity is 2048 events, so the
    ///     count cap is impractical here; a single oversized payload trims the list and advances the floor in one step.
    /// </summary>
    private void TrimToFloor(Guid executionId)
    {
        var buffer = Factory.Services.GetRequiredService<IIntegrationExecutionEventBuffer>();
        var payload = JsonSerializer.SerializeToElement(new
        {
            text = new string('a', count: 5 * 1024 * 1024)
        });
        _ = buffer.Append(executionId, Guid.NewGuid(), IntegrationStreamEventTypes.AssistantDelta, contentType: null, payload);
        AssertEx.True(buffer.Floor(executionId) > 1, "The trim must have moved the floor, or the below-the-floor arm is not what is being tested.");
    }

    private async Task<Seeded> SeedAsync(HttpClient client, string prefix)
    {
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(Factory, $"{prefix}-agent");
        var trigger = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, $"{prefix}-a", agentId);
        var other = await IntegrationEndpointPayloads.CreateTriggerAsync(Factory, client, $"{prefix}-b", agentId);

        var broad = await IntegrationEndpointPayloads.GenerateKeyAsync(Factory, client, $"{prefix}-broad");
        var narrow = await IntegrationEndpointPayloads.GenerateKeyAsync(Factory, client, $"{prefix}-narrow", [other.Id], broad.View.PrincipalId);
        var foreign = await IntegrationEndpointPayloads.GenerateKeyAsync(Factory, client, $"{prefix}-foreign");

        return new Seeded(trigger.Name, trigger.Id, broad.View.PrincipalId, broad.View.KeyPrefix, broad.Key, narrow.Key, foreign.Key);
    }

    /// <summary>
    ///     Writes an admitted row through the real store rather than the invoke route, so the suite asserts on a stable
    ///     execution instead of racing a background run that has no model to reach.
    /// </summary>
    private async Task<Guid> SeedExecutionAsync(Guid triggerId, Guid principalId, string keyPrefix, bool tracked, bool active = false)
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
        var executionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var admitted = await store.AcceptAsync(new IntegrationAcceptCommand(new IntegrationSessionCreate(sessionId, triggerId, Guid.NewGuid(), Guid.NewGuid()),
                executionId,
                triggerId,
                sessionId,
                principalId,
                Guid.NewGuid(),
                new byte[] { 1, 2, 3 },
                keyPrefix,
                now,
                new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 1, IntegrationStreamEventTypes.ExecutionAccepted, DetailJson: null, now)),
            maxActive: 4096,
            maxActivePerPrincipal: 4096);
        AssertEx.True(admitted, "Seeding the execution row must be admitted.");

        if (tracked)
        {
            // The ring's own entry, holding execution.accepted at sequence 1 exactly as the accept path leaves it — a
            // reader attaching from 0 must be served, and it would not be if the ring's floor started at 2.
            var buffer = Factory.Services.GetRequiredService<IIntegrationExecutionEventBuffer>();
            AssertEx.True(buffer.TryCreate(executionId));
            AssertEx.Equal(expected: 1L, buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionAccepted, contentType: null, payload: null).Sequence);
        }

        if (!active)
        {
            // Closed on the way out: the factory is shared across this class, and a row left Accepted counts against
            // the node-wide admission cap for every later test in it.
            AssertEx.True(await store.TryTerminalizeAsync(new IntegrationTerminalizeCommand(executionId,
                ExpectedVersion: 0,
                new HashSet<IntegrationExecutionStatus> { IntegrationExecutionStatus.Accepted },
                IntegrationExecutionStatus.Completed,
                Sequence: 1_000,
                IntegrationStreamEventTypes.ExecutionCompleted,
                now,
                FailureCategory: null,
                FailureSummary: null)));
        }

        return executionId;
    }

    private async Task PersistEventAsync(Guid executionId, long sequence, string eventType, string? detailJson)
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>()
                   .AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), executionId, sequence, eventType, detailJson, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private static async Task<string> ReadFirstFrameAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var frame = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                break;
            }

            _ = frame.AppendLine(line);
        }

        return frame.ToString();
    }

    private static IReadOnlyList<long> Sequences(string body) =>
        [
            .. body.Split('\n')
                   .Where(static line => line.StartsWith("id: ", StringComparison.Ordinal))
                   .Select(static line => long.Parse(line[4..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
        ];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client,
        HttpMethod method,
        string route,
        string? key,
        string? accept = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        string? lastEventId = null)
    {
        using var request = new HttpRequestMessage(method, route);
        if (key is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {key}");
        }

        if (accept is not null)
        {
            request.Headers.Add("Accept", accept);
        }

        if (lastEventId is not null)
        {
            request.Headers.Add("Last-Event-ID", lastEventId);
        }

        using var content = method == HttpMethod.Post
            ? JsonContent.Create(new
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
            })
            : null;
        request.Content = content;
        return await client.SendAsync(request, completion);
    }

    private sealed record Seeded(string TriggerName,
        Guid TriggerId,
        Guid PrincipalId,
        string KeyPrefix,
        string BroadKey,
        string NarrowKey,
        string ForeignKey);

    private sealed record EventListBody(IReadOnlyList<EventBody> Items);

    private sealed record EventBody(Guid ExecutionId, long Sequence, string EventType, string? DetailJson, long OccurredAtUtc);

    private sealed record StatusLinksBody(Guid ExecutionId, LinksBody Links);

    private sealed record LinksBody(string Self, string Events);
}

/// <summary>
///     A host whose stream cap is one, so the open-stream gate is reachable at the route. The cap is
///     <c>MaxTrackedExecutions</c> by design (R3-11 adds no twelfth knob), which is why lowering it needs its own host
///     rather than a per-test knob.
/// </summary>
public sealed class IntegrationOneStreamHostFixture : IAsyncInitializer, IAsyncDisposable
{
    public TestServerWebAppFactory Factory { get; } = new()
    {
        AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Integrations:MaxTrackedExecutions"] = "1"
        }
    };

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();
}

/// <summary>Test 35a — the open-stream gate, as a status rather than as a queued connection.</summary>
[NotInParallel("IntegrationOneStreamHost")]
public sealed class IntegrationSseStreamCapTests
{
    [ClassDataSource<IntegrationOneStreamHostFixture>(Shared = SharedType.PerClass)]
    public required IntegrationOneStreamHostFixture Host { get; init; }

    [Test]
    public async Task Events_WhenEveryStreamSlotIsHeld_Answers503WithRetryAfterAndAJsonBody()
    {
        var factory = Host.Factory;
        using var client = factory.CreateClient();
        var agentId = await IntegrationEndpointPayloads.SeedAgentAsync(factory, "stream-cap-agent");
        var trigger = await IntegrationEndpointPayloads.CreateTriggerAsync(factory, client, "stream-cap", agentId);
        var key = await IntegrationEndpointPayloads.GenerateKeyAsync(factory, client, "stream-cap-key");
        var executionId = await SeedTrackedExecutionAsync(factory, trigger.Id, key.View.PrincipalId, key.View.KeyPrefix);

        // Held open on purpose: the writer's slot is released in its finally, which only runs when the stream ends.
        using var held = await SendAsync(client, key.Key, executionId, HttpCompletionOption.ResponseHeadersRead);
        AssertEx.Equal(HttpStatusCode.OK, held.StatusCode);

        using var refused = await SendAsync(client, key.Key, executionId, HttpCompletionOption.ResponseContentRead);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode,
            "A caller that cannot be served now is told so, rather than parked holding a connection.");
        AssertEx.Equal("5", refused.Headers.RetryAfter?.ToString());
        AssertEx.Equal("application/json", refused.Content.Headers.ContentType?.MediaType, "The refusal is a status, never a partially written stream.");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string key, Guid executionId, HttpCompletionOption completion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, IntegrationApiRoutes.Events(executionId));
        request.Headers.Add("Authorization", $"Bearer {key}");
        request.Headers.Add("Accept", "text/event-stream");
        return await client.SendAsync(request, completion);
    }

    private static async Task<Guid> SeedTrackedExecutionAsync(TestServerWebAppFactory factory, Guid triggerId, Guid principalId, string keyPrefix)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
        var executionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AssertEx.True(await store.AcceptAsync(new IntegrationAcceptCommand(new IntegrationSessionCreate(sessionId, triggerId, Guid.NewGuid(), Guid.NewGuid()),
                executionId,
                triggerId,
                sessionId,
                principalId,
                Guid.NewGuid(),
                new byte[] { 1, 2, 3 },
                keyPrefix,
                now,
                new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 1, IntegrationStreamEventTypes.ExecutionAccepted, DetailJson: null, now)),
            maxActive: 4096,
            maxActivePerPrincipal: 4096));

        // Non-terminal on purpose: a terminal event would end the held stream and free the slot the test is about.
        var buffer = factory.Services.GetRequiredService<IIntegrationExecutionEventBuffer>();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = buffer.Append(executionId, sessionId, IntegrationStreamEventTypes.ExecutionAccepted, contentType: null, payload: null);
        return executionId;
    }
}
