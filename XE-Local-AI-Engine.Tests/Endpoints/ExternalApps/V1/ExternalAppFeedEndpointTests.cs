namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalApps.V1;

using System.Net;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Tests.Containers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two read-only feeds: the paged event history and the bounded log tail. What they prove is that paging is
///     driven by an EXCLUSIVE watermark the caller can resume from without overlap or gap, that a tail above the cap is
///     refused rather than quietly shrunk, and that neither feed ever carries an engine-owned value.
/// </summary>
public sealed class ExternalAppFeedEndpointTests
{
    private const string LogText = "odysseus ready on :8080";

    [Test]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceEvents)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceLogs)]
    public async Task Route_WithoutAToken_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = Factory(Substitute.For<IExternalAppService>());

        using var response = await ExternalAppEndpointPayloads.SendAnonymousAsync(factory, method, route).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require a token.");
    }

    [Test]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceEvents)]
    [Arguments("GET", ExternalAppEndpointPayloads.InstanceLogs)]
    public async Task Route_WithANonOperatorToken_ReturnsForbidden(string method, string route)
    {
        await using var factory = Factory(Substitute.For<IExternalAppService>());

        using var response = await ExternalAppEndpointPayloads.SendAsNonOperatorAsync(factory, method, route).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {route} is operator-only.");
    }

    /// <summary>
    ///     The watermark is EXCLUSIVE and the page is ascending: a caller resuming from sequence 4 is shown 5 first,
    ///     never 4 again.
    /// </summary>
    [Test]
    public async Task Events_PageFromAnExclusiveWatermark_Ascending()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ListEventsAsync(ExternalAppEndpointPayloads.InstanceId, 4L, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Event(5, ExternalAppInstanceEventKind.StartRequested), Event(6, ExternalAppInstanceEventKind.Started)]);

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceEvents}?afterSequence=4&limit=2")
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = document.RootElement.GetProperty("items");
        AssertEx.Equal(2, items.GetArrayLength());
        AssertEx.Equal(5, items[0].GetProperty("sequence").GetInt64(), "the oldest row of the page comes first.");
        AssertEx.Equal(6, items[1].GetProperty("sequence").GetInt64());
        AssertEx.Equal("StartRequested", items[0].GetProperty("kind").GetString());
        AssertEx.Equal(6, document.RootElement.GetProperty("highestSequence").GetInt64(), "the caller resumes from the last row it was shown.");
        AssertEx.False(document.RootElement.GetProperty("hasMore").GetBoolean());

        await apps.Received(1)
                  .ListEventsAsync(ExternalAppEndpointPayloads.InstanceId, 4L, Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .ConfigureAwait(false);
    }

    /// <summary>
    ///     <c>hasMore</c> is OBSERVED: the endpoint asks for one row past the limit, so a history that is exactly a
    ///     multiple of the page size does not report a page that is not there.
    /// </summary>
    [Test]
    public async Task Events_ReportHasMore_FromOneRowPastTheLimit()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Event(1), Event(2), Event(3)]);

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceEvents}?limit=2")
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(2, document.RootElement.GetProperty("items").GetArrayLength(), "the extra row is the probe, not a page member.");
        AssertEx.True(document.RootElement.GetProperty("hasMore").GetBoolean());
        AssertEx.Equal(2, document.RootElement.GetProperty("highestSequence").GetInt64());

        await apps.Received(1)
                  .ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), 3, Arg.Any<CancellationToken>())
                  .ConfigureAwait(false);
    }

    /// <summary>The second page starts strictly after the first page's last sequence: no overlap and no gap.</summary>
    [Test]
    public async Task Events_SecondPage_ContinuesWithoutOverlapOrGap()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ListEventsAsync(Arg.Any<Guid>(), 0L, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Event(1), Event(2), Event(3)]);
        apps.ListEventsAsync(Arg.Any<Guid>(), 2L, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Event(3), Event(4)]);

        await using var factory = Factory(apps);

        using var first = await ExternalAppEndpointPayloads
                                .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceEvents}?afterSequence=0&limit=2")
                                .ConfigureAwait(false);
        using var firstBody = await ExternalAppEndpointPayloads.ReadJsonAsync(first).ConfigureAwait(false);
        var watermark = firstBody.RootElement.GetProperty("highestSequence").GetInt64();

        using var second = await ExternalAppEndpointPayloads
                                 .SendAsOperatorAsync(factory,
                                     "GET",
                                     $"{ExternalAppEndpointPayloads.InstanceEvents}?afterSequence={watermark}&limit=2")
                                 .ConfigureAwait(false);
        using var secondBody = await ExternalAppEndpointPayloads.ReadJsonAsync(second).ConfigureAwait(false);

        AssertEx.Equal(2L, watermark);
        var items = secondBody.RootElement.GetProperty("items");
        AssertEx.Equal(2, items.GetArrayLength());
        AssertEx.Equal(3, items[0].GetProperty("sequence").GetInt64(), "the next page opens at the row after the watermark.");
        AssertEx.Equal(4, items[1].GetProperty("sequence").GetInt64());
        AssertEx.False(secondBody.RootElement.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>An empty page leaves the caller's own watermark in place, so the next request cannot skip a new row.</summary>
    [Test]
    public async Task Events_EmptyPage_EchoesTheRequestedWatermark()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceEvents}?afterSequence=9")
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
        AssertEx.Equal(9, document.RootElement.GetProperty("highestSequence").GetInt64());
        AssertEx.False(document.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Test]
    [Arguments("?afterSequence=-1", "afterSequence")]
    [Arguments("?limit=0", "limit")]
    [Arguments("?limit=501", "limit")]
    public async Task Events_WithAnOutOfRangeBound_ReturnsBadRequestWithoutReadingTheFeed(string query, string member)
    {
        var apps = Substitute.For<IExternalAppService>();

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstanceEvents + query)
                                   .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, member, StringComparison.Ordinal, "the 400 names the wire member.");
        await apps.DidNotReceive()
                  .ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .ConfigureAwait(false);
    }

    /// <summary>An unknown instance is a 404, never an empty page a caller would render as "nothing has happened yet".</summary>
    [Test]
    public async Task Events_ForAnUnknownInstance_ReturnsNotFound()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ExternalAppInstanceEventSnapshot>>(_ => throw new ExternalAppNotFoundException("No such instance."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstanceEvents)
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    ///     The event feed is content-free by contract: it carries the sequence, the instant and the kind, and nothing
    ///     an instance's variables could have reached.
    /// </summary>
    [Test]
    public async Task Events_CarryNoVariableValue()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Event(1, ExternalAppInstanceEventKind.Installed, "{\"service\":\"web\"}")]);

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstanceEvents)
                                   .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(body.Contains("a-real-password", StringComparison.Ordinal), "an event body is content-free by contract.");
        AssertEx.False(body.Contains(ExternalAppEndpointPayloads.SecretVariableName, StringComparison.Ordinal),
            "not even the NAME of a secret variable belongs in the feed.");
    }

    /// <summary>
    ///     The tail cap is a REJECTION, not a clamp: a caller that asked for a window the node does not serve has to
    ///     learn that, rather than receive a shorter log it would read as the whole one.
    /// </summary>
    [Test]
    [Arguments(2000, HttpStatusCode.OK)]
    [Arguments(2001, HttpStatusCode.BadRequest)]
    [Arguments(0, HttpStatusCode.BadRequest)]
    public async Task Logs_AtAndAboveTheTailCap_AreServedOrRefusedButNeverClamped(int tail, HttpStatusCode expected)
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ReadLogsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Snapshot());

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceLogs}?service=web&tail={tail}")
                                   .ConfigureAwait(false);

        AssertEx.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            await apps.Received(1)
                      .ReadLogsAsync(ExternalAppEndpointPayloads.InstanceId, "web", tail, Arg.Any<CancellationToken>())
                      .ConfigureAwait(false);
        }
        else
        {
            await apps.DidNotReceive()
                      .ReadLogsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                      .ConfigureAwait(false);
        }
    }

    /// <summary>The named service is echoed back, so the caller can refresh the same window it is reading.</summary>
    [Test]
    public async Task Logs_EchoTheNamedServiceAndTheDaemonText()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ReadLogsAsync(Arg.Any<Guid>(), "worker", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Snapshot(truncated: true, lineCount: 12));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceLogs}?service=worker")
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("worker", document.RootElement.GetProperty("service").GetString());
        AssertEx.Equal(LogText, document.RootElement.GetProperty("text").GetString(), "the daemon's text crosses verbatim.");
        AssertEx.Equal(12, document.RootElement.GetProperty("lineCount").GetInt32());
        AssertEx.True(document.RootElement.GetProperty("truncated").GetBoolean(), "truncation is reported, never inferred.");
    }

    /// <summary>
    ///     With no <c>?service=</c> the response still names the service it read — the first one publishing a port —
    ///     because a null there would leave the caller unable to name the window it is looking at.
    /// </summary>
    [Test]
    public async Task Logs_WithoutAService_NameTheResolvedDefault()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ReadLogsAsync(Arg.Any<Guid>(), null, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Snapshot());
        apps.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ExternalAppEndpointPayloads.Detail());

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstanceLogs)
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("web", document.RootElement.GetProperty("service").GetString(), "the first service publishing a port is the default.");
    }

    /// <summary>
    ///     The log tail is the ONE read that reaches <c>IContainerRuntimeResolver.CreateRuntimeAsync</c> inside the
    ///     request, so a node whose daemon is down answers 503 with the resolution's own operator prose. A 500 here
    ///     would tell the operator the engine broke rather than that the machine has an obstacle to clear. The throw is
    ///     staged on the substituted service because that is exactly what the real
    ///     <c>ExternalAppService.ReadLogsAsync</c> does with it: it lets the resolver's exception leave unhandled.
    /// </summary>
    [Test]
    public async Task Logs_WhenNoContainerRuntimeIsAvailable_ReturnServiceUnavailable()
    {
        var resolution = ExternalAppEndpointPayloads.Resolution(ContainerRuntimeStatus.PermissionDenied);
        var apps = Substitute.For<IExternalAppService>();
        apps.ReadLogsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ContainerLogSnapshot>(_ => throw new ContainerRuntimeUnavailableException(resolution));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceLogs}?service=web")
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertEx.Equal(resolution.Message,
            document.RootElement.GetProperty("detail").GetString(),
            "the body carries the resolution's operator message, not a daemon exception message.");
    }

    /// <summary>
    ///     The same 503, for the resolution the REAL resolver writes when the endpoint itself hides a secret. This is
    ///     the path the redaction has to hold on: the refusal text becomes the <c>detail</c> of a ProblemDetails body
    ///     that any operator-gated caller can read, so a message that repeated the value back would publish it.
    /// </summary>
    [Test]
    public async Task Logs_WhenTheRuntimeWasRefusedForASecretInItsEndpoint_TheProblemBodyCarriesNeitherHalfOfIt()
    {
        var resolution = await DisclosingEndpointRefusal.ResolveAsync().ConfigureAwait(false);
        var apps = Substitute.For<IExternalAppService>();
        apps.ReadLogsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ContainerLogSnapshot>(_ => throw new ContainerRuntimeUnavailableException(resolution));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceLogs}?service=web")
                                   .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertEx.False(body.Contains(DisclosingEndpointRefusal.QuerySentinel, StringComparison.Ordinal),
            $"The 503 body carries the query string's value: {body}");
        AssertEx.False(body.Contains(DisclosingEndpointRefusal.FragmentSentinel, StringComparison.Ordinal),
            $"The 503 body carries the fragment's value: {body}");
        AssertEx.Contains(body, "a query string", StringComparison.Ordinal);
    }

    /// <summary>A service the manifest does not declare is a 404 from the service, not an empty log.</summary>
    [Test]
    public async Task Logs_ForAnUnknownService_ReturnNotFound()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ReadLogsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ContainerLogSnapshot>(_ => throw new ExternalAppNotFoundException("This application declares no service named 'ghost'."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceLogs}?service=ghost")
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Logs_ForAnUnknownInstance_ReturnNotFound()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ReadLogsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ContainerLogSnapshot>(_ => throw new ExternalAppNotFoundException("No such instance."));

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", ExternalAppEndpointPayloads.InstanceLogs)
                                   .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    ///     The log response carries the daemon's text and nothing else the engine owns: no variables, masked or
    ///     otherwise, ride along with it.
    /// </summary>
    [Test]
    public async Task Logs_CarryNoEngineOwnedValue()
    {
        var apps = Substitute.For<IExternalAppService>();
        apps.ReadLogsAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Snapshot());

        await using var factory = Factory(apps);

        using var response = await ExternalAppEndpointPayloads
                                   .SendAsOperatorAsync(factory, "GET", $"{ExternalAppEndpointPayloads.InstanceLogs}?service=web")
                                   .ConfigureAwait(false);
        using var document = await ExternalAppEndpointPayloads.ReadJsonAsync(response).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(document.RootElement.TryGetProperty("variables", out _), "a log read carries no instance variables.");
        AssertEx.False(body.Contains(ExternalAppEndpointPayloads.SecretVariableName, StringComparison.Ordinal));
    }

    private static ExternalAppInstanceEventSnapshot Event(long sequence,
        ExternalAppInstanceEventKind kind = ExternalAppInstanceEventKind.Started,
        string? detailJson = null) =>
        new(Guid.NewGuid(), ExternalAppEndpointPayloads.InstanceId, sequence, kind, detailJson, 1_780_000_000_000L + sequence);

    private static ContainerLogSnapshot Snapshot(bool truncated = false, int lineCount = 1) =>
        new()
        {
            Text = LogText,
            Truncated = truncated,
            LineCount = lineCount
        };

    private static TestServerWebAppFactory Factory(IExternalAppService apps) =>
        ExternalAppEndpointPayloads.EnabledFactory(apps);
}
