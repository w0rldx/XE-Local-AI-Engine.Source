namespace XE_Local_AI_Engine.Tests.Chat;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Round-trips the distilled conversation state through <see cref="INodeChatPersistenceService.SetConversationStateAsync" />
///     against real SQLite: the JSON reads back decrypted, a null clears the whole triple, and the stored column is
///     ciphertext bound to its own column name.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeChatConversationStateTests : IDisposable
{
    private const string StateJson = "{\"version\":1,\"entries\":[{\"id\":\"e1\",\"category\":\"Goal\",\"value\":\"ship the distiller\"}]}";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task SetConversationStateAsync_RoundTripsTheJsonAndWatermark()
    {
        await using var provider = await BuildProviderAsync("state-roundtrip.sqlite");
        var service = CreateService(provider);
        var conversationId = await CreateConversationAsync(service);

        var returned = AssertEx.NotNull(await service.SetConversationStateAsync(new NodeChatSetConversationStateRequest
        {
            ConversationId = conversationId,
            State = StateJson,
            CoversToSequence = 4,
            UpdatedAtUtc = 50
        }));
        AssertEx.Equal(StateJson, returned.ConversationState);

        var read = AssertEx.NotNull(await service.GetConversationAsync(conversationId));
        AssertEx.Equal(StateJson, read.ConversationState);
        AssertEx.Equal<int?>(4, read.ConversationStateCoversToSequence);
        AssertEx.Equal<long?>(50, read.ConversationStateUpdatedAtUtc);
        AssertEx.Null(read.CompactionSummary);
    }

    [Test]
    public async Task SetConversationStateAsync_WithNullState_ClearsTheStateAndStampsTheTimestamp()
    {
        await using var provider = await BuildProviderAsync("state-clear.sqlite");
        var service = CreateService(provider);
        var conversationId = await CreateConversationAsync(service);
        await SetStateAsync(service, conversationId);

        await service.SetConversationStateAsync(new NodeChatSetConversationStateRequest
        {
            ConversationId = conversationId,
            State = null,
            CoversToSequence = 9,
            UpdatedAtUtc = 70
        });

        var read = AssertEx.NotNull(await service.GetConversationAsync(conversationId));
        AssertEx.Null(read.ConversationState);
        AssertEx.Null(read.ConversationStateCoversToSequence);
        AssertEx.Equal<long?>(70, read.ConversationStateUpdatedAtUtc, "A clear stamps the timestamp so a stale writer can see it.");
    }

    [Test]
    public async Task SetConversationStateAsync_WhenGuardedOnAStaleStamp_WritesNothingAndReturnsNull()
    {
        await using var provider = await BuildProviderAsync("state-guard.sqlite");
        var service = CreateService(provider);
        var conversationId = await CreateConversationAsync(service);
        await SetStateAsync(service, conversationId); // stamps 50

        var rejected = await service.SetConversationStateAsync(new NodeChatSetConversationStateRequest
        {
            ConversationId = conversationId,
            State = "{\"version\":1,\"entries\":[]}",
            CoversToSequence = 9,
            UpdatedAtUtc = 60,
            GuardUnchanged = true,
            ExpectedUpdatedAtUtc = 49
        });
        var accepted = await service.SetConversationStateAsync(new NodeChatSetConversationStateRequest
        {
            ConversationId = conversationId,
            State = "{\"version\":1,\"entries\":[]}",
            CoversToSequence = 9,
            UpdatedAtUtc = 60,
            GuardUnchanged = true,
            ExpectedUpdatedAtUtc = 50
        });

        AssertEx.Null(rejected);
        var written = AssertEx.NotNull(accepted);
        AssertEx.Equal<long?>(60, written.ConversationStateUpdatedAtUtc);
        AssertEx.Equal<int?>(9, written.ConversationStateCoversToSequence);
    }

    [Test]
    public async Task SetConversationStateAsync_ForAnUnknownConversation_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("state-unknown.sqlite");
        var service = CreateService(provider);

        AssertEx.Null(await service.SetConversationStateAsync(new NodeChatSetConversationStateRequest
        {
            ConversationId = Guid.NewGuid(),
            State = StateJson,
            CoversToSequence = 1,
            UpdatedAtUtc = 50
        }));
    }

    [Test]
    public async Task SetConversationStateAsync_StoresCiphertextBoundToTheStateColumn()
    {
        await using var provider = await BuildProviderAsync("state-encrypted.sqlite");
        var service = CreateService(provider);
        var conversationId = await CreateConversationAsync(service);
        await SetStateAsync(service, conversationId);

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_state FROM conversations WHERE conversation_id = $id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);
        var blob = AssertEx.NotNull(await command.ExecuteScalarAsync() as byte[], "The state column must hold a BLOB.");

        var stored = Encoding.UTF8.GetString(blob);
        AssertEx.False(stored.Contains("ship the distiller", StringComparison.Ordinal), "The state column must not hold plaintext.");
        AssertEx.Equal(StateJson, dbContext.DecryptConversationState(blob, conversationId));

        // The AAD names the column, so the blob cannot be replayed as a compaction synopsis.
        AssertEx.Throws<CryptographicException>(() => dbContext.DecryptConversationCompactionSummary(blob, conversationId));
    }

    [Test]
    public async Task SetSelectedPathAsync_ClearsTheConversationState()
    {
        // The state is distilled from the previously selected path, so a path change invalidates it like the synopsis.
        await using var provider = await BuildProviderAsync("state-path-change.sqlite");
        var service = CreateService(provider);
        var conversationId = await CreateConversationAsync(service);
        await SetStateAsync(service, conversationId);
        AssertEx.NotNull(AssertEx.NotNull(await service.GetConversationAsync(conversationId)).ConversationState);

        await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest
        {
            ConversationId = conversationId,
            SelectedPath = new Dictionary<Guid, Guid>(),
            UpdatedAtUtc = 80
        });

        var read = AssertEx.NotNull(await service.GetConversationAsync(conversationId));
        AssertEx.Null(read.ConversationState);
        AssertEx.Null(read.ConversationStateCoversToSequence);
        AssertEx.Equal<long?>(80, read.ConversationStateUpdatedAtUtc, "The clear is stamped so an in-flight distillation's guarded write is rejected.");
    }

    [Test]
    public async Task CreateMessageVariantAsync_ClearsTheConversationState()
    {
        // Minting a sibling shifts the default selected path, so state distilled from the prior selection is stale.
        await using var provider = await BuildProviderAsync("state-variant.sqlite");
        var service = CreateService(provider);
        var conversationId = await CreateConversationAsync(service);
        var assistantMessageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest
        {
            ConversationId = conversationId,
            MessageId = Guid.NewGuid(),
            Content = "hi",
            CreatedAtUtc = 11
        });
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest
        {
            ConversationId = conversationId,
            MessageId = assistantMessageId,
            RequestId = Guid.NewGuid(),
            CreatedAtUtc = 12,
            Model = "llama"
        });
        await SetStateAsync(service, conversationId);
        AssertEx.NotNull(AssertEx.NotNull(await service.GetConversationAsync(conversationId)).ConversationState);

        await service.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest
        {
            ConversationId = conversationId,
            OriginalMessageId = assistantMessageId,
            NewMessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            CreatedAtUtc = 14
        });

        var read = AssertEx.NotNull(await service.GetConversationAsync(conversationId));
        AssertEx.Null(read.ConversationState);
        AssertEx.Null(read.ConversationStateCoversToSequence);
        AssertEx.Equal<long?>(14, read.ConversationStateUpdatedAtUtc, "The clear is stamped so an in-flight distillation's guarded write is rejected.");
    }

    private static async Task SetStateAsync(NodeChatPersistenceService service, Guid conversationId)
    {
        await service.SetConversationStateAsync(new NodeChatSetConversationStateRequest
        {
            ConversationId = conversationId,
            State = StateJson,
            CoversToSequence = 4,
            UpdatedAtUtc = 50
        });
    }

    private static async Task<Guid> CreateConversationAsync(NodeChatPersistenceService service)
    {
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "State",
            UserId = "node",
            CreatedAtUtc = 10
        });
        return conversation.ConversationId;
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(_rootPath, fileName)}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();
        return provider;
    }

    private static NodeChatPersistenceService CreateService(ServiceProvider provider)
    {
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
    }
}
