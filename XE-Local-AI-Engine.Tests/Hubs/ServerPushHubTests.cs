namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     End-to-end coverage for the three server-push-only hubs — <see cref="KnowledgeBaseHub" />,
///     <see cref="GgufDownloadHub" /> and <see cref="RuntimeAcquisitionHub" />. They expose no client-callable methods,
///     so what has to be proven is the surface around them: the negotiate is operator-gated (each hub leaks the shape of
///     local state — which documents exist, which models are being fetched), and the hub-backed publisher that replaces
///     the no-op default actually reaches a connected client under the agreed event name and payload.
/// </summary>
public sealed class ServerPushHubTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    [Arguments(LocalApiRoutes.KnowledgeBase.Hub)]
    [Arguments(LocalApiRoutes.ModelFit.DownloadHub)]
    [Arguments(LocalApiRoutes.ModelFit.LlamaCppAcquisitionHub)]
    public async Task Negotiate_WhenTokenMissing_ReturnsUnauthorized(string hubPath)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, hubPath + "/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    [Arguments(typeof(KnowledgeBaseHub))]
    [Arguments(typeof(GgufDownloadHub))]
    [Arguments(typeof(RuntimeAcquisitionHub))]
    public void Hub_RequiresTheOperatorPolicyOnTheJwtScheme(Type hubType)
    {
        var authorize = AssertEx.NotNull(hubType.GetCustomAttribute<AuthorizeAttribute>());

        AssertEx.Equal(NodeAuthorizationPolicies.Operator, authorize.Policy);
        AssertEx.Equal(JwtBearerDefaults.AuthenticationScheme, authorize.AuthenticationSchemes);
    }

    [Test]
    [Arguments(typeof(KnowledgeBaseHub))]
    [Arguments(typeof(GgufDownloadHub))]
    [Arguments(typeof(RuntimeAcquisitionHub))]
    public void Hub_ExposesNoClientCallableServerMethods(Type hubType)
    {
        // These hubs are push-only by design. A public instance method declared on one would silently become an
        // operator-invokable RPC, so the absence is asserted rather than assumed.
        var declared = hubType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        AssertEx.Empty(declared);
    }

    [Test]
    public async Task KnowledgeIndexingNotifier_PushIsReceivedByAnAuthorizedClient()
    {
        var documentId = Guid.NewGuid();
        await using var connection = Connect(LocalApiRoutes.KnowledgeBase.Hub);
        var received = new TaskCompletionSource<KnowledgeDocumentChangedHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<KnowledgeDocumentChangedHubEvent>(KnowledgeBaseHubEvents.DocumentChanged, evt => received.TrySetResult(evt));
        await connection.StartAsync();

        await Factory.Services.GetRequiredService<IKnowledgeIndexingNotifier>()
                     .NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Embedding);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        AssertEx.Equal(KnowledgeBaseHubEvents.DocumentChanged, evt.EventType);
        AssertEx.Equal(documentId, evt.DocumentId);
        AssertEx.Equal(KnowledgeDocumentStatus.Embedding, evt.Status);
    }

    [Test]
    public async Task GgufDownloadEventPublisher_PushIsReceivedByAnAuthorizedClient()
    {
        await using var connection = Connect(LocalApiRoutes.ModelFit.DownloadHub);
        var received = new TaskCompletionSource<GgufDownloadStatusHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<GgufDownloadStatusHubEvent>(GgufDownloadHubEvents.StatusChanged, evt => received.TrySetResult(evt));
        await connection.StartAsync();

        var published = new GgufDownloadStatusHubEvent("qwen3.5-0.8b-q4_k_m.gguf",
            "Running",
            CompletedBytes: 512,
            TotalBytes: 4096,
            SanitizedError: null);
        await Factory.Services.GetRequiredService<IGgufDownloadEventPublisher>().PublishStatusAsync(published);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        AssertEx.Equal(published.ModelName, evt.ModelName);
        AssertEx.Equal("Running", evt.Phase);
        AssertEx.Equal(expected: 512L, evt.CompletedBytes);
        AssertEx.Equal(expected: 4096L, evt.TotalBytes);
    }

    [Test]
    public async Task RuntimeAcquisitionEventPublisher_PushIsReceivedByAnAuthorizedClient()
    {
        await using var connection = Connect(LocalApiRoutes.ModelFit.LlamaCppAcquisitionHub);
        var received = new TaskCompletionSource<RuntimeAcquisitionStatusHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<RuntimeAcquisitionStatusHubEvent>(RuntimeAcquisitionHubEvents.StatusChanged, evt => received.TrySetResult(evt));
        await connection.StartAsync();

        var published = new RuntimeAcquisitionStatusHubEvent(Sequence: 7,
            nameof(RuntimeAcquisitionPhase.Downloading),
            "Cuda",
            "b10201",
            CompletedBytes: 1024,
            TotalBytes: 8192,
            StepIndex: 1,
            StepCount: 2,
            SanitizedError: null);
        await Factory.Services.GetRequiredService<IRuntimeAcquisitionEventPublisher>().PublishStatusAsync(published);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        AssertEx.Equal(expected: 7L, evt.Sequence);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Downloading), evt.Phase);
        AssertEx.Equal("Cuda", evt.Variant);
        AssertEx.Equal("b10201", evt.Tag);
        AssertEx.Equal(expected: 2, evt.StepCount);
    }

    private HubConnection Connect(string hubPath)
    {
        var factory = Factory;
        return new HubConnectionBuilder()
               .WithUrl("http://localhost" + hubPath, options =>
               {
                   options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                   options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                   options.Headers.Add("Origin", "http://localhost");
               })
               .Build();
    }
}
