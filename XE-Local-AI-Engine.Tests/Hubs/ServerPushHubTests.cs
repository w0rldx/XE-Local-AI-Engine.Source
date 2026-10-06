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
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     End-to-end coverage for the three server-push-only hubs — <see cref="KnowledgeBaseHub" />,
///     <see cref="GgufDownloadHub" /> and <see cref="RuntimeAcquisitionHub" />. They expose no client-callable methods,
///     so what has to be proven is the surface around them: the negotiate is operator-gated (each hub leaks the shape of
///     local state — which documents exist, which models are being fetched), and the hub-backed publisher that replaces
///     the no-op default actually reaches a connected client under the agreed event name and payload.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ServerPushHubTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    [Arguments(LocalApiRoutes.KnowledgeBase.Hub)]
    [Arguments(LocalApiRoutes.ModelFit.DownloadHub)]
    [Arguments(LocalApiRoutes.ModelFit.LlamaCppAcquisitionHub)]
    [Arguments(LocalApiRoutes.ModelFit.ResidencyHub)]
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
    [Arguments(typeof(RuntimeResidencyHub))]
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
    [Arguments(typeof(RuntimeResidencyHub))]
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
        await connection.StartAndAwaitRegistrationAsync();

        await Factory.Services.GetRequiredService<IKnowledgeIndexingNotifier>()
                     .NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Embedding);

        var evt = await received.Task.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(KnowledgeBaseHubEvents.DocumentChanged, evt.EventType);
        AssertEx.Equal(documentId, evt.DocumentId);
        AssertEx.Equal(KnowledgeDocumentStatus.Embedding, evt.Status);
    }

    [Test]
    public async Task GgufDownloadEventPublisher_PushIsReceivedByAnAuthorizedClient()
    {
        await using var connection = Connect(LocalApiRoutes.ModelFit.DownloadHub);
        var received = new TaskCompletionSource<GgufDownloadStatusHubMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<GgufDownloadStatusHubMessage>(GgufDownloadHubEvents.StatusChanged, evt => received.TrySetResult(evt));
        await connection.StartAndAwaitRegistrationAsync();

        var published = new GgufAcquisitionStatus
        {
            OperationId = Guid.NewGuid(),
            OperationKind = GgufAcquisitionOperationKind.Download,
            ModelName = "qwen3.5-0.8b-q4_k_m.gguf",
            Phase = GgufAcquisitionPhase.Running,
            CompletedBytes = 512,
            TotalBytes = 4096,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            ErrorCode = null,
            SanitizedError = null
        };
        await Factory.Services.GetRequiredService<IGgufDownloadEventPublisher>().PublishStatusAsync(published);

        var evt = await received.Task.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(published.ModelName, evt.ModelName);
        AssertEx.Equal("Running", evt.Phase);
        AssertEx.Equal(expected: 512L, evt.CompletedBytes);
        AssertEx.Equal(expected: 4096L, evt.TotalBytes);
    }

    [Test]
    public async Task RuntimeAcquisitionEventPublisher_PushIsReceivedByAnAuthorizedClient()
    {
        await using var connection = Connect(LocalApiRoutes.ModelFit.LlamaCppAcquisitionHub);
        var received = new TaskCompletionSource<RuntimeAcquisitionStatusEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<RuntimeAcquisitionStatusEvent>(RuntimeAcquisitionHubEvents.StatusChanged, evt => received.TrySetResult(evt));
        await connection.StartAndAwaitRegistrationAsync();

        var published = new RuntimeAcquisitionStatusEvent
        {
            Sequence = 7,
            Phase = nameof(RuntimeAcquisitionPhase.Downloading),
            Variant = "Cuda",
            Tag = "b10201",
            CompletedBytes = 1024,
            TotalBytes = 8192,
            StepIndex = 1,
            StepCount = 2,
            SanitizedError = null
        };
        await Factory.Services.GetRequiredService<IRuntimeAcquisitionEventPublisher>().PublishStatusAsync(published);

        var evt = await received.Task.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(expected: 7L, evt.Sequence);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Downloading), evt.Phase);
        AssertEx.Equal("Cuda", evt.Variant);
        AssertEx.Equal("b10201", evt.Tag);
        AssertEx.Equal(expected: 2, evt.StepCount);
    }

    [Test]
    public async Task RuntimeResidencyChangeNotifier_TickIsReceivedByAnAuthorizedClient()
    {
        await using var connection = Connect(LocalApiRoutes.ModelFit.ResidencyHub);
        var received = new TaskCompletionSource<RuntimeResidencyChangedHubMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<RuntimeResidencyChangedHubMessage>(RuntimeResidencyHubEvents.Changed, evt => received.TrySetResult(evt));
        await connection.StartAndAwaitRegistrationAsync();

        // The host's registration, not the provider no-op, is what every supervisor and gate resolves.
        var notifier = Factory.Services.GetRequiredService<IRuntimeResidencyChangeNotifier>();
        AssertEx.True(notifier is RuntimeResidencyChangePublisher, $"Resolved {notifier.GetType().Name}, not the hub-backed publisher.");
        notifier.NotifyChanged();

        var evt = await received.Task.WaitAsync(TestBudgets.Contended);
        AssertEx.True(evt.Sequence >= 1);
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
               .WithNodeJsonProtocol()
               .Build();
    }}
