namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     End-to-end coverage for the server-push-only hubs <see cref="KnowledgeBaseHub" />,
///     <see cref="GgufDownloadHub" /> and <see cref="RuntimeAcquisitionHub" />: the negotiate is operator-gated, and
///     the hub-backed publisher reaches a connected client under the agreed event name and payload.
/// </summary>
/// <remarks>They expose no client-callable methods; each leaks the shape of local state (documents, fetches).</remarks>
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

    [Test]
    public async Task RenameThroughTheApi_PushesAConversationChangeOnTheChatHub()
    {
        // An open sidebar in another client refreshes on this push instead of waiting for a reload.
        var conversationId = Guid.NewGuid();
        _ = await Factory.Services.GetRequiredService<INodeChatPersistenceService>().EnsureConversationAsync(new NodeChatEnsureConversationRequest
        {
            ConversationId = conversationId,
            Title = "Before",
            UserId = "operator",
            CreatedAtUtc = 10,
            Origin = NodeChatOriginValues.Local
        });
        await using var connection = Connect(LocalApiRoutes.LocalChat.Hub);
        var received = new TaskCompletionSource<ConversationsChangedHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<ConversationsChangedHubEvent>(LocalChatHubEvents.ConversationsChanged, evt =>
        {
            // Turns finishing in parallel tests on this shared host push too.
            if (evt.ConversationId == conversationId)
            {
                received.TrySetResult(evt);
            }
        });
        await connection.StartAndAwaitRegistrationAsync();

        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/local/v1/chat/conversations/{conversationId}/rename")
        {
            Content = JsonContent.Create(new
            {
                Title = "After"
            })
        };
        Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var evt = await received.Task.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(LocalChatHubEvents.ConversationsChanged, evt.EventType);
        AssertEx.Equal<Guid?>(conversationId, evt.ConversationId);
    }

    [Test]
    public async Task AFinishedTurn_PushesOneConversationChange_AndItsStreamedWritesPushNone()
    {
        // A turn another client writes re-sorts and re-previews an open sidebar once, at its end, not per flushed token.
        var service = Factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var conversationId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation
        {
            ConversationId = conversationId,
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid()
        };
        await using var connection = Connect(LocalApiRoutes.LocalChat.Hub);
        var pushes = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<ConversationsChangedHubEvent>(LocalChatHubEvents.ConversationsChanged, evt =>
        {
            if (evt.ConversationId == conversationId)
            {
                _ = Interlocked.Increment(ref pushes);
                received.TrySetResult();
            }
        });
        await connection.StartAndAwaitRegistrationAsync();

        _ = await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest
        {
            ConversationId = conversationId,
            Title = "Turn push",
            UserId = "operator",
            CreatedAtUtc = 10,
            Origin = NodeChatOriginValues.Local
        });
        _ = await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest
        {
            ConversationId = conversationId,
            MessageId = correlation.MessageId,
            RequestId = correlation.RequestId,
            CreatedAtUtc = 12
        });
        _ = await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 13);
        _ = await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest
        {
            Correlation = correlation,
            Content = "partial",
            Reasoning = null,
            UpdatedAtUtc = 14
        });
        _ = await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 15,
            Content = "answer"
        });

        await received.Task.WaitAsync(TestBudgets.Contended);
        // One connection delivers in send order, so a push from an earlier write would have arrived before this one.
        AssertEx.Equal(expected: 1, Volatile.Read(ref pushes), "Only the terminal write pushes; the placeholder, streaming mark and flush do not.");
    }

    [Test]
    public async Task AFinishedTurnInAWorkSessionTranscript_PushesNothing()
    {
        // Only chats are listed: a work-session, integration or graph turn must not make every open tab re-read the list.
        var service = Factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var workSession = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Work session turn",
            UserId = "operator",
            CreatedAtUtc = 10,
            Kind = NodeConversationKind.WorkSession
        });
        var marker = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Marker chat turn",
            UserId = "operator",
            CreatedAtUtc = 11
        });
        await using var connection = Connect(LocalApiRoutes.LocalChat.Hub);
        var workSessionPushes = 0;
        var markerReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<ConversationsChangedHubEvent>(LocalChatHubEvents.ConversationsChanged, evt =>
        {
            if (evt.ConversationId == workSession.ConversationId)
            {
                _ = Interlocked.Increment(ref workSessionPushes);
            }
            else if (evt.ConversationId == marker.ConversationId)
            {
                markerReceived.TrySetResult();
            }
        });
        await connection.StartAndAwaitRegistrationAsync();

        await FinishTurnAsync(service, workSession.ConversationId);
        await FinishTurnAsync(service, marker.ConversationId);

        await markerReceived.Task.WaitAsync(TestBudgets.Contended);
        // One connection delivers in send order: a push for the earlier work-session turn would have arrived first.
        AssertEx.Equal(expected: 0, Volatile.Read(ref workSessionPushes), "A work-session turn must not push a chat-list change.");
    }

    private static async Task FinishTurnAsync(INodeChatPersistenceService service, Guid conversationId)
    {
        var correlation = new NodeChatMessageCorrelation
        {
            ConversationId = conversationId,
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid()
        };
        _ = await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest
        {
            ConversationId = conversationId,
            MessageId = correlation.MessageId,
            RequestId = correlation.RequestId,
            CreatedAtUtc = 12
        });
        _ = await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 15,
            Content = "answer"
        });
    }

    [Test]
    public async Task ImportWithAnImportId_PushesItsProgress_ThenFinished()
    {
        var importId = Guid.NewGuid();
        await using var connection = Connect(LocalApiRoutes.LocalChat.Hub);
        var events = new List<ImportProgressHubEvent>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<ImportProgressHubEvent>(LocalChatHubEvents.ImportProgress, evt =>
        {
            if (evt.ImportId != importId)
            {
                return;
            }

            lock (events)
            {
                events.Add(evt);
            }

            if (evt.State == ImportProgressStates.Finished)
            {
                finished.TrySetResult();
            }
        });
        await connection.StartAndAwaitRegistrationAsync();

        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/local/v1/chat/import?importId={importId}")
        {
            Content = JsonContent.Create(new NodeChatExportDocument
            {
                Format = NodeChatExportFile.Format,
                FormatVersion = NodeChatExportFile.FormatVersion,
                Conversations = [ImportedChat("one"), ImportedChat("two"), ImportedChat("three")]
            })
        };
        Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        await finished.Task.WaitAsync(TestBudgets.Contended);
        string observed;
        lock (events)
        {
            observed = string.Join(' ', events.Select(static evt => $"{evt.State}:{evt.ImportedChats}/{evt.TotalChats}"));
        }

        AssertEx.Equal("running:0/3 running:1/3 running:2/3 finished:3/3", observed, "A start, one push per written chat, and the end.");
    }

    private static NodeChatExportConversation ImportedChat(string title) =>
        new()
        {
            Title = title,
            CreatedAtUtc = 10,
            Messages =
            [
                new NodeChatExportMessage
                {
                    Role = "user",
                    Content = title,
                    CreatedAtUtc = 11
                }
            ]
        };

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
    }
}
