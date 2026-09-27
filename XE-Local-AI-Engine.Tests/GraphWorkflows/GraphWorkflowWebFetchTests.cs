namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The one web exception to the Tool-node envelope (ADR 0017, decision 6): <c>web_fetch</c> on a node carrying a
///     link allow-list, checked at save for what the author typed and at dispatch for what the run computed.
/// </summary>
/// <remarks>
///     Each test takes a host of its own, because web access is a node setting a test may flip. Two seams are replaced:
///     the node settings and the fetch service's HTTP transport, so no request leaves the box. The gate, the executor,
///     the fetch service's allow-list and SSRF checks, the store and the dispatcher are the real ones.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowWebFetchTests
{
    private const string Allowed = "https://docs.example.com/guide";

    [Test]
    public void Parse_AllowedUrlsThatAreNotAnArrayOfStrings_AreRefused()
    {
        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Graph("web_fetch", """, "allowedUrls": ["https://a.example.com/", 3]""")));

        AssertEx.Contains(refusal.Message, "allowedUrls");
    }

    [Test]
    public void Parse_AllowedUrls_AreReadTrimmedAndDefaultToEmpty()
    {
        var listed = AssertEx.NotNull(GraphWorkflowGraph.Parse(Graph("web_fetch", """, "allowedUrls": ["  https://a.example.com/x  "]""")).Nodes["fetch"].Config
            as GraphWorkflowToolConfig);
        var none = AssertEx.NotNull(GraphWorkflowGraph.Parse(Graph("read_file")).Nodes["fetch"].Config as GraphWorkflowToolConfig);

        AssertEx.Equal("https://a.example.com/x", string.Join(",", listed.AllowedUrls));
        AssertEx.Empty(none.AllowedUrls, "never null: a fetch handed no list must fail closed, not open.");
    }

    [Test]
    public async Task ListTools_WithWebAccessOn_OffersWebFetchFlaggedAsNeedingAllowedLinks_AndNeverWebSearch()
    {
        await using var web = WebHost.Create();

        var tools = await web.Definitions.ListToolsAsync();

        var fetch = AssertEx.NotNull(tools.SingleOrDefault(static tool => tool.Name == "web_fetch"), "the picker offers web_fetch while web access is on.");
        AssertEx.True(fetch.RequiresAllowedUrls);
        AssertEx.Contains(fetch.ParameterSchema, "url");
        AssertEx.False(tools.Any(static tool => tool.Name == "web_search"));
        AssertEx.False(tools.Where(static tool => tool.Name != "web_fetch").Any(static tool => tool.RequiresAllowedUrls), "only web_fetch carries the flag.");
    }

    [Test]
    public async Task Save_WithWebAccessOff_RefusesWebFetchAndThePickerHidesIt()
    {
        await using var web = WebHost.Create(webAccessEnabled: false);

        var error = await SingleErrorAsync(web, Graph("web_fetch", $$""", "allowedUrls": ["{{Allowed}}"]"""));

        AssertEx.Contains(error, "web access");
        AssertEx.False((await web.Definitions.ListToolsAsync()).Any(static tool => tool.Name == "web_fetch"));
    }

    [Test]
    public async Task Save_WebFetchWithoutAllowedLinks_IsRefusedKeyedToTheNode()
    {
        await using var web = WebHost.Create();

        var error = await SingleErrorAsync(web, Graph("web_fetch", """, "argumentBindings": { "url": "run.input.url" }"""));

        AssertEx.Contains(error, "at least one allowed link");
    }

    [Test]
    [Arguments("ftp://files.example.com/")]
    [Arguments("/docs")]
    [Arguments("   ")]
    public async Task Save_AnAllowedLinkThatIsNotAnAbsoluteHttpUrl_IsRefused(string entry)
    {
        await using var web = WebHost.Create();

        var error = await SingleErrorAsync(web, Graph("web_fetch", $$""", "allowedUrls": ["{{Allowed}}", "{{entry}}"]"""));

        AssertEx.Contains(error, "not an absolute http or https URL");
    }

    [Test]
    public async Task Save_ALiteralUrlOutsideTheAllowedLinks_IsRefused()
    {
        await using var web = WebHost.Create();

        // A shared prefix that is not a path-segment boundary: "/guide-private" is not under "/guide".
        var error = await SingleErrorAsync(web,
            Graph("web_fetch", $$""", "allowedUrls": ["{{Allowed}}"], "arguments": { "url": "https://docs.example.com/guide-private" }"""));

        AssertEx.Contains(error, "not under any of its allowed links");
    }

    [Test]
    public async Task Save_WebSearchOnAToolNode_IsRefusedWhileWebAccessIsOn()
    {
        await using var web = WebHost.Create();

        var error = await SingleErrorAsync(web, Graph("web_search", """, "arguments": { "query": "tides" }"""));

        AssertEx.Contains(error, "web_search");
        AssertEx.Contains(error, "never available to a Tool node");
    }

    [Test]
    public async Task Save_AllowedLinksOnAnotherTool_AreRefused()
    {
        await using var web = WebHost.Create();

        var error = await SingleErrorAsync(web, Graph("read_file", $$""", "arguments": { "path": "a.md" }, "allowedUrls": ["{{Allowed}}"]"""));

        AssertEx.Contains(error, "Only a 'web_fetch' Tool node reads 'allowedUrls'");
    }

    /// <summary>The field is additive to schema version 1: the stored graph comes back byte-for-byte, list included.</summary>
    [Test]
    public async Task Create_ThenGet_RoundTripsTheAllowedLinksVerbatim()
    {
        await using var web = WebHost.Create();
        var graph = Graph("web_fetch", $$""", "allowedUrls": ["{{Allowed}}", "https://status.example.com/"], "arguments": { "url": "{{Allowed}}/retries" }""");

        var created = await web.Definitions.CreateAsync($"Web {Guid.NewGuid():N}", description: null, graph);
        var stored = await web.Definitions.GetAsync(created.Id);

        AssertEx.Equal(graph, stored.GraphJson);
    }

    [Test]
    public async Task Dispatch_ABoundUrlUnderTheAllowedLinks_FetchesItAndSucceedsWithFencedContent()
    {
        await using var web = WebHost.Create();
        var runId = await web.Harness.StartRunAsync(BoundGraph(), """{"url":"https://docs.example.com/guide/retries"}""");

        var fetch = await SettleAsync(web.Harness, runId);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, fetch.Status, fetch.Error);
        AssertEx.Equal("https://docs.example.com/guide/retries", string.Join(",", web.Transport.Requests));
        var result = Result(fetch);
        AssertEx.Equal(UntrustedContentFraming.UntrustedTrustLabel, result.GetProperty("contentTrust").GetString());
        AssertEx.Contains(result.GetProperty("content").GetString(), "page at /guide/retries");
    }

    /// <summary>
    ///     Dispatch is the real gate: a URL an upstream node computed is not something the save could see. It fails the
    ///     node before any request is made, and without echoing the URL back.
    /// </summary>
    [Test]
    public async Task Dispatch_ABoundUrlOutsideTheAllowedLinks_FailsValidationWithoutARequest()
    {
        await using var web = WebHost.Create();
        var runId = await web.Harness.StartRunAsync(BoundGraph(), """{"url":"https://attacker.example.net/?q=secret"}""");

        var fetch = await SettleAsync(web.Harness, runId);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, fetch.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, fetch.FailureClass);
        AssertEx.Contains(fetch.Error, "not in this node's allowed links");
        AssertEx.False(fetch.Error!.Contains("secret", StringComparison.Ordinal), "the reason never quotes the URL.");
        AssertEx.Empty(web.Transport.Requests, "nothing left the box.");
    }

    [Test]
    public async Task Dispatch_ARedirectLeavingTheAllowedLinks_FailsAfterTheFirstHopOnly()
    {
        await using var web = WebHost.Create();
        var runId = await web.Harness.StartRunAsync(BoundGraph(), """{"url":"https://docs.example.com/guide/moved"}""");

        var fetch = await SettleAsync(web.Harness, runId);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, fetch.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, fetch.FailureClass);
        AssertEx.Contains(fetch.Error, "redirected to a URL that is not in this node's allowed links");
        AssertEx.Equal("https://docs.example.com/guide/moved", string.Join(",", web.Transport.Requests), "the escaping hop was never requested.");
    }

    /// <summary>The kill switch, turned off between the start's gate and the dispatch: the service's refusal is the node's failure.</summary>
    [Test]
    public async Task Dispatch_AfterWebAccessWasTurnedOff_FailsWithTheServicesRefusal()
    {
        await using var web = WebHost.Create();
        var runId = await web.Harness.StartRunAsync(BoundGraph(), """{"url":"https://docs.example.com/guide/retries"}""");
        web.Settings.GetWebAccessEnabledAsync(Arg.Any<CancellationToken>()).Returns(false);

        var fetch = await SettleAsync(web.Harness, runId);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, fetch.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, fetch.FailureClass, "the switch answers the same on a retry.");
        AssertEx.Contains(fetch.Error, "Web access is disabled");
        AssertEx.Empty(web.Transport.Requests);
    }

    private static string BoundGraph() =>
        Graph("web_fetch", $$""", "allowedUrls": ["{{Allowed}}"], "argumentBindings": { "url": "run.input.url" }""");

    /// <summary>A linear <c>Start → Tool → End</c> graph whose tool node, <c>fetch</c>, the caller configures.</summary>
    private static string Graph(string toolName, string? toolConfig = null) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "fetch", "kind": "Tool", "maxAttempts": 1, "config": { "toolName": "{{toolName}}"{{toolConfig}} } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "fetch" },
              { "key": "e2", "from": "fetch", "to": "done" }
            ]
          }
          """;

    private static async Task<string> SingleErrorAsync(WebHost web, string graph)
    {
        var result = await web.Definitions.ValidateAsync(graph);
        var error = AssertEx.NotNull(result.Errors.SingleOrDefault(), "exactly one refusal");
        AssertEx.Equal("fetch", error.Key, "keyed by the node, so the editor draws it there.");
        return error.Message;
    }

    private static async Task<GraphWorkflowNodeRunSnapshot> SettleAsync(GraphWorkflowHarness harness, Guid runId)
    {
        await harness.AdvanceUntilAsync(runId,
            async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadNodeRunAsync(runId, "fetch")).Status),
            $"Run {runId} left its web_fetch node unsettled.");
        return await harness.ReadNodeRunAsync(runId, "fetch");
    }

    private static JsonElement Result(GraphWorkflowNodeRunSnapshot nodeRun)
    {
        using var document = JsonDocument.Parse(AssertEx.NotNull(nodeRun.OutputJson, "a settled tool node always carries its output document."));
        return document.RootElement.GetProperty("output").GetProperty("result").Clone();
    }

    /// <summary>A private host with web access on (or off) and a fetch service whose transport never leaves the process.</summary>
    private sealed class WebHost : IAsyncDisposable
    {
        private WebHost(GraphWorkflowHarness harness, RecordingTransport transport, INodeRuntimeSettings settings)
        {
            Harness = harness;
            Transport = transport;
            Settings = settings;
        }

        public GraphWorkflowHarness Harness { get; }

        public RecordingTransport Transport { get; }

        public INodeRuntimeSettings Settings { get; }

        public IGraphWorkflowDefinitionService Definitions => Harness.Services.GetRequiredService<IGraphWorkflowDefinitionService>();

        public static WebHost Create(bool webAccessEnabled = true)
        {
            var transport = new RecordingTransport();
            var settings = StubNodeRuntimeSettings.Create().WithWebAccessEnabled(webAccessEnabled).Build();
            var clients = Substitute.For<IHttpClientFactory>();
            clients.CreateClient(WebFetchService.HttpClientName).Returns(_ => new HttpClient(transport, disposeHandler: false));
            var harness = GraphWorkflowHarness.PrivateHost(services =>
            {
                services.RemoveAll<INodeRuntimeSettings>();
                services.AddSingleton(settings);
                services.RemoveAll<WebFetchService>();
                services.AddSingleton(new WebFetchService(clients, settings, TimeProvider.System, NullLogger<WebFetchService>.Instance));
            });
            return new WebHost(harness, transport, settings);
        }

        public async ValueTask DisposeAsync()
        {
            await Harness.DisposeAsync();
            Transport.Dispose();
        }
    }

    /// <summary>Answers <c>/guide/moved</c> with a redirect off the list, and anything else with a short text page naming its path.</summary>
    private sealed class RecordingTransport : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();

        public IReadOnlyList<string> Requests => [.. _requests];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            _requests.Enqueue(url.AbsoluteUri);
            if (url.AbsolutePath == "/guide/moved")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://elsewhere.example.org/landing");
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"page at {url.AbsolutePath}", Encoding.UTF8, "text/plain")
            });
        }
    }
}
