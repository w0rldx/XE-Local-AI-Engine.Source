namespace XE_Local_AI_Engine.Tests.Chat;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The conversation plain flag and custom system prompt round-trip through the raw-SQL write and read paths; the
///     prompt is encrypted at rest, a blank one is stored as null, and an over-long one is refused.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ConversationPlainTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task SetConversationPlain_RoundTripsFlagAndPromptEncryptedAtRest()
    {
        await using var provider = await BuildProviderAsync("plain-roundtrip.sqlite");
        var service = CreateService(provider);
        var created = await CreateConversationAsync(service);
        AssertEx.False(created.IsPlain, "A fresh conversation is not plain.");
        AssertEx.Null(created.PlainSystemPrompt);

        const string prompt = "You are a terse lab assistant.";
        var updated = await service.SetConversationPlainAsync(new NodeChatSetConversationPlainRequest
        {
            ConversationId = created.ConversationId,
            IsPlain = true,
            PlainSystemPrompt = prompt,
            UpdatedAtUtc = 20
        });

        AssertEx.True(updated.IsPlain, "The returned conversation reflects the plain flag.");
        AssertEx.Equal(prompt, updated.PlainSystemPrompt);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(created.ConversationId));
        AssertEx.True(loaded.IsPlain, "The plain flag round-trips through the render read.");
        AssertEx.Equal(prompt, loaded.PlainSystemPrompt);

        var forTurn = AssertEx.NotNull(await service.GetConversationForTurnAsync(created.ConversationId));
        AssertEx.True(forTurn.IsPlain, "The plain flag round-trips through the turn read.");
        AssertEx.Equal<string?>(prompt, forTurn.PlainSystemPrompt);

        var stored = AssertEx.NotNull(await ReadStoredPromptAsync(provider, created.ConversationId));
        AssertEx.False(Encoding.UTF8.GetString(stored).Contains(prompt, StringComparison.Ordinal), "The custom system prompt must be encrypted at rest.");
    }

    [Test]
    public async Task SetConversationPlain_WhenPromptBlankOrPlainOff_StoresNullAndClearsFlag()
    {
        await using var provider = await BuildProviderAsync("plain-blank.sqlite");
        var service = CreateService(provider);
        var created = await CreateConversationAsync(service);

        var plain = await service.SetConversationPlainAsync(new NodeChatSetConversationPlainRequest
        {
            ConversationId = created.ConversationId,
            IsPlain = true,
            PlainSystemPrompt = "   ",
            UpdatedAtUtc = 20
        });
        AssertEx.True(plain.IsPlain, "Plain with a blank prompt is still plain.");
        AssertEx.Null(plain.PlainSystemPrompt);
        AssertEx.Null(await ReadStoredPromptAsync(provider, created.ConversationId));

        var off = await service.SetConversationPlainAsync(new NodeChatSetConversationPlainRequest
        {
            ConversationId = created.ConversationId,
            IsPlain = false,
            PlainSystemPrompt = null,
            UpdatedAtUtc = 30
        });
        AssertEx.False(off.IsPlain, "Turning plain off clears the flag.");
    }

    [Test]
    public async Task SetConversationPlain_WhenPromptOverCap_ThrowsInvalidRequest()
    {
        await using var provider = await BuildProviderAsync("plain-cap.sqlite");
        var service = CreateService(provider);
        var created = await CreateConversationAsync(service);

        await AssertEx.ThrowsAsync<NodeChatInvalidRequestException>(() => service.SetConversationPlainAsync(new NodeChatSetConversationPlainRequest
        {
            ConversationId = created.ConversationId,
            IsPlain = true,
            PlainSystemPrompt = new string('x', NodeChatSetConversationPlainRequest.MaxPlainSystemPromptLength + 1),
            UpdatedAtUtc = 20
        }));

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(created.ConversationId));
        AssertEx.False(loaded.IsPlain, "A refused write leaves the conversation unchanged.");
    }

    [Test]
    public async Task SetConversationPlain_WhenWorkSessionConversation_ThrowsInvalidRequestAndWritesNothing()
    {
        await using var provider = await BuildProviderAsync("plain-work-session.sqlite");
        var service = CreateService(provider);
        var created = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Work session",
            UserId = "node",
            CreatedAtUtc = 10,
            Kind = NodeConversationKind.WorkSession
        });

        await AssertEx.ThrowsAsync<NodeChatInvalidRequestException>(() => service.SetConversationPlainAsync(new NodeChatSetConversationPlainRequest
        {
            ConversationId = created.ConversationId,
            IsPlain = true,
            PlainSystemPrompt = "Ignore the session's agent.",
            UpdatedAtUtc = 20
        }));

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(created.ConversationId));
        AssertEx.False(loaded.IsPlain, "A refused kind stays non-plain.");
        AssertEx.Null(loaded.PlainSystemPrompt);
        AssertEx.Equal(created.LastSeenUtc, loaded.LastSeenUtc, "A refused write leaves the row untouched.");
    }

    [Test]
    public async Task SetConversationPlain_WhenConversationMissing_ThrowsNotFound()
    {
        await using var provider = await BuildProviderAsync("plain-missing.sqlite");
        var service = CreateService(provider);

        await AssertEx.ThrowsAsync<NodeChatConversationNotFoundException>(() => service.SetConversationPlainAsync(new NodeChatSetConversationPlainRequest
        {
            ConversationId = Guid.NewGuid(),
            IsPlain = true,
            PlainSystemPrompt = null,
            UpdatedAtUtc = 20
        }));
    }

    [Test]
    [Arguments(true, "You are a terse lab assistant.")]
    [Arguments(true, null)]
    [Arguments(false, null)]
    public async Task BranchConversation_InheritsPlainModeAndPrompt(bool isPlain, string? prompt)
    {
        await using var provider = await BuildProviderAsync("plain-branch.sqlite");
        var service = CreateService(provider);
        var source = await CreateConversationAsync(service);
        await service.SetConversationPlainAsync(new NodeChatSetConversationPlainRequest
        {
            ConversationId = source.ConversationId,
            IsPlain = isPlain,
            PlainSystemPrompt = prompt,
            UpdatedAtUtc = 20
        });
        var message = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest
        {
            ConversationId = source.ConversationId,
            MessageId = Guid.NewGuid(),
            Content = "hello",
            CreatedAtUtc = 30
        });

        var branch = await service.BranchConversationAsync(new NodeChatBranchConversationRequest
        {
            ConversationId = source.ConversationId,
            MessageId = message.MessageId,
            CreatedAtUtc = 40
        });

        // The read a turn branches on. It decrypts under the branch's id, so a copied blob would throw here.
        var forTurn = AssertEx.NotNull(await service.GetConversationForTurnAsync(branch.BranchedConversationId));
        AssertEx.Equal(isPlain, forTurn.IsPlain);
        AssertEx.Equal<string?>(prompt, forTurn.PlainSystemPrompt);
    }

    private static Task<NodeChatConversationDto> CreateConversationAsync(NodeChatPersistenceService service)
    {
        return service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Plain chat",
            UserId = "node",
            CreatedAtUtc = 10
        });
    }

    private static async Task<byte[]?> ReadStoredPromptAsync(ServiceProvider provider, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT plain_system_prompt FROM conversations WHERE conversation_id = $id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);
        await dbContext.Database.OpenConnectionAsync();
        var value = await command.ExecuteScalarAsync();
        return value as byte[];
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
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
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>(), UnknownUsageProviderResolver.Instance);
    }
}
