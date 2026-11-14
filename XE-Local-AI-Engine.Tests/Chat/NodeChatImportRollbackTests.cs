namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A chat import whose write fails partway: every chat it created is purged, a purge that fails too does not stop the
///     others, and the request answers with the error that caused the rollback, never a rollback error.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeChatImportRollbackTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Import_WhenTheThirdChatsInsertFails_PurgesWhatItWrote_AndSurfacesTheInsertError(bool firstPurgeFails)
    {
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeChatPersistenceService>();
                services.AddSingleton<NodeChatPersistenceService>();
                services.AddSingleton(provider => FailingImportPersistence.Wrap(provider.GetRequiredService<NodeChatPersistenceService>(), firstPurgeFails));
            }
        };
        var suffix = Guid.NewGuid().ToString("N");
        var document = new NodeChatExportDocument
        {
            Format = NodeChatExportFile.Format,
            FormatVersion = NodeChatExportFile.FormatVersion,
            Conversations = [Chat($"First {suffix}", "fine"), Chat($"Second {suffix}", "fine"), Chat($"Third {suffix}", FailingImportPersistence.FailingContent)]
        };
        using var client = factory.CreateClient();
        var importId = Guid.NewGuid();
        await using var connection = new HubConnectionBuilder()
                                     .WithUrl("http://localhost" + LocalApiRoutes.LocalChat.Hub, options =>
                                     {
                                         options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                         options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                                         options.Headers.Add("Origin", "http://localhost");
                                     })
                                     .WithNodeJsonProtocol()
                                     .Build();
        var failed = new TaskCompletionSource<ImportProgressHubEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = connection.On<ImportProgressHubEvent>(LocalChatHubEvents.ImportProgress, evt =>
        {
            if (evt.ImportId == importId && evt.State == ImportProgressStates.Failed)
            {
                failed.TrySetResult(evt);
            }
        });
        await connection.StartAndAwaitRegistrationAsync();

        using var request = Request(factory, HttpMethod.Post, $"/api/local/v1/chat/import?importId={importId}");
        request.Content = JsonContent.Create(document, options: JsonOptions);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, "The insert's own error must answer the request, not a purge failure (a 500).");
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), FailingImportPersistence.FailureMarker.ToString());
        var failure = await failed.Task.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal("2/3", $"{failure.ImportedChats}/{failure.TotalChats}", "The progress ends as failed, after the two chats written before the third failed.");
        var remaining = (await factory.Services.GetRequiredService<INodeChatPersistenceService>().ListConversationsAsync(new NodeChatListConversationsRequest
                        {
                            IncludeArchived = true
                        }))
                        .Where(conversation => conversation.Title?.EndsWith(suffix, StringComparison.Ordinal) == true)
                        .Select(static conversation => conversation.Title)
                        .ToList();
        AssertEx.Equal(firstPurgeFails ? $"First {suffix}" : string.Empty, string.Join('|', remaining),
            "Every chat the import created is purged, except one whose own purge failed, which must not stop the others.");
    }

    private static NodeChatExportConversation Chat(string title, string content) =>
        new()
        {
            Title = title,
            CreatedAtUtc = 10,
            Messages =
            [
                new NodeChatExportMessage
                {
                    Role = "user",
                    Content = content,
                    CreatedAtUtc = 11
                }
            ]
        };

    private static HttpRequestMessage Request(TestServerWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}

/// <summary>
///     The container's chat persistence with two faults: an insert of <see cref="FailingContent" /> throws an error
///     the API maps to a 400, and, when asked, the first delete throws.
/// </summary>
/// <remarks>Not sealed: <see cref="DispatchProxy" /> derives the proxy type at run time. Every other member forwards untouched.</remarks>
public class FailingImportPersistence : DispatchProxy
{
    public const string FailingContent = "this insert fails";

    public static readonly Guid FailureMarker = Guid.Parse("5f0c3a4e-7d2b-4c19-9a61-2e8b7f4d1c05");

    private INodeChatPersistenceService _inner = null!;
    private int _deletesSeen;
    private bool _failFirstDelete;

    public static INodeChatPersistenceService Wrap(INodeChatPersistenceService inner, bool failFirstDelete)
    {
        var proxy = Create<INodeChatPersistenceService, FailingImportPersistence>();
        var self = (FailingImportPersistence)(object)proxy;
        self._inner = inner;
        self._failFirstDelete = failFirstDelete;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (args is [NodeChatInsertMessageIfAbsentRequest { Content: FailingContent }, ..])
        {
            throw new NodeChatInvalidBranchSelectionException(Guid.Empty, Guid.Empty, FailureMarker);
        }

        if (targetMethod.Name == nameof(INodeChatPersistenceService.DeleteConversationAsync)
            && _failFirstDelete
            && Interlocked.Increment(ref _deletesSeen) == 1)
        {
            throw new InvalidOperationException("Simulated purge failure during the import rollback.");
        }

        return targetMethod.Invoke(_inner, args);
    }
}
