namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A conversation's own sampling settings round-trip through the raw-SQL write and both reads as plain JSON, clear
///     with null, are refused on a non-chat conversation and are copied when the conversation is branched.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ConversationSamplingOptionsTests : IDisposable
{
    private static readonly SamplingOptions Settings = new()
    {
        Temperature = 0.4f,
        TopK = 20,
        MaxOutputTokens = 512,
        Seed = "42",
        Stop = ["###"]
    };

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task SetSamplingOptions_RoundTripsThroughBothReads_AsPlainJsonWithoutUnsetKnobs()
    {
        await using var provider = await BuildProviderAsync("sampling-roundtrip.sqlite");
        var service = CreateService(provider);
        var created = await CreateConversationAsync(service);
        AssertEx.Null(created.SamplingOptions, "A fresh conversation follows the client's defaults.");

        var updated = await SetAsync(service, created.ConversationId, Settings);

        AssertSettings(updated.SamplingOptions);
        AssertSettings((await service.GetConversationAsync(created.ConversationId))?.SamplingOptions);
        AssertSettings((await service.GetConversationForTurnAsync(created.ConversationId))?.SamplingOptions);
        AssertEx.Equal("""{"temperature":0.4,"topK":20,"maxOutputTokens":512,"seed":"42","stop":["###"]}""",
            await ReadStoredJsonAsync(provider, created.ConversationId));
    }

    [Test]
    public async Task SetSamplingOptions_WithNull_ClearsTheColumn()
    {
        await using var provider = await BuildProviderAsync("sampling-clear.sqlite");
        var service = CreateService(provider);
        var created = await CreateConversationAsync(service);
        await SetAsync(service, created.ConversationId, Settings);

        var cleared = await SetAsync(service, created.ConversationId, null);

        AssertEx.Null(cleared.SamplingOptions);
        AssertEx.Null(await ReadStoredJsonAsync(provider, created.ConversationId));
    }

    [Test]
    public async Task SetSamplingOptions_WhenWorkSessionConversation_ThrowsInvalidRequestAndWritesNothing()
    {
        await using var provider = await BuildProviderAsync("sampling-work-session.sqlite");
        var service = CreateService(provider);
        var created = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Work session",
            UserId = "node",
            CreatedAtUtc = 10,
            Kind = NodeConversationKind.WorkSession
        });

        await AssertEx.ThrowsAsync<NodeChatInvalidRequestException>(() => SetAsync(service, created.ConversationId, Settings));

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(created.ConversationId));
        AssertEx.Null(loaded.SamplingOptions);
        AssertEx.Equal(created.LastSeenUtc, loaded.LastSeenUtc, "A refused write leaves the row untouched.");
    }

    [Test]
    public async Task SetSamplingOptions_WhenConversationMissing_ThrowsNotFound()
    {
        await using var provider = await BuildProviderAsync("sampling-missing.sqlite");
        var service = CreateService(provider);

        await AssertEx.ThrowsAsync<NodeChatConversationNotFoundException>(() => SetAsync(service, Guid.NewGuid(), Settings));
    }

    [Test]
    [Arguments("""{"temperature":"hot"}""")]
    [Arguments("""{"seed":42}""")]
    [Arguments("not json")]
    public async Task UnreadableStoredSettings_ReadAsTheClientDefaults_AndTheChatStillLoads(string storedJson)
    {
        await using var provider = await BuildProviderAsync("sampling-unreadable.sqlite");
        var service = CreateService(provider);
        var created = await CreateConversationAsync(service);
        await WriteStoredJsonAsync(provider, created.ConversationId, storedJson);

        AssertEx.Null(AssertEx.NotNull(await service.GetConversationAsync(created.ConversationId)).SamplingOptions);
        AssertEx.Null(AssertEx.NotNull(await service.GetConversationForTurnAsync(created.ConversationId)).SamplingOptions);
        var renamed = await service.RenameConversationAsync(new NodeChatRenameConversationRequest
        {
            ConversationId = created.ConversationId,
            Title = "Renamed",
            UpdatedAtUtc = 30
        });
        AssertEx.Null(renamed.SamplingOptions, "The post-write read tolerates the bad row as well.");
        AssertEx.Equal(storedJson, await ReadStoredJsonAsync(provider, created.ConversationId), "A read never rewrites the stored value.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task BranchConversation_CopiesTheSamplingOptions(bool withSettings)
    {
        await using var provider = await BuildProviderAsync("sampling-branch.sqlite");
        var service = CreateService(provider);
        var source = await CreateConversationAsync(service);
        if (withSettings)
        {
            await SetAsync(service, source.ConversationId, Settings);
        }

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

        var branched = AssertEx.NotNull(await service.GetConversationAsync(branch.BranchedConversationId));
        if (withSettings)
        {
            AssertSettings(branched.SamplingOptions);
        }
        else
        {
            AssertEx.Null(branched.SamplingOptions, "A branch of a conversation without settings follows the client's defaults too.");
        }
    }

    private static void AssertSettings(SamplingOptions? actual)
    {
        var settings = AssertEx.NotNull(actual);
        AssertEx.Equal(Settings.Temperature, settings.Temperature);
        AssertEx.Equal(Settings.TopK, settings.TopK);
        AssertEx.Equal(Settings.MaxOutputTokens, settings.MaxOutputTokens);
        AssertEx.Equal("42", settings.Seed);
        AssertEx.Equal("###", AssertEx.NotNull(settings.Stop).Single());
        AssertEx.Null(settings.TopP, "An unset knob stays unset.");
    }

    private static Task<NodeChatConversationDto> SetAsync(NodeChatPersistenceService service, Guid conversationId, SamplingOptions? samplingOptions)
    {
        return service.SetConversationSamplingOptionsAsync(new NodeChatSetConversationSamplingOptionsRequest
        {
            ConversationId = conversationId,
            SamplingOptions = samplingOptions,
            UpdatedAtUtc = 20
        });
    }

    private static Task<NodeChatConversationDto> CreateConversationAsync(NodeChatPersistenceService service)
    {
        return service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Sampling chat",
            UserId = "node",
            CreatedAtUtc = 10
        });
    }

    private static async Task<string?> ReadStoredJsonAsync(ServiceProvider provider, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sampling_options_json FROM conversations WHERE conversation_id = $id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);
        await dbContext.Database.OpenConnectionAsync();
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task WriteStoredJsonAsync(ServiceProvider provider, Guid conversationId, string json)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.ExecuteSqlAsync($"UPDATE conversations SET sampling_options_json = {json} WHERE conversation_id = {conversationId};");
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
