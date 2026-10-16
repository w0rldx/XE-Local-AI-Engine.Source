namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

[Category(TestCategories.Integration)]
public sealed class PersistenceEncryptionTests : IDisposable
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
    public async Task SaveChanges_WhenEncryptedPayloadsPersisted_RoundTripsThroughSqlite()
    {
        var databasePath = GetDatabasePath();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var toolCallId = Guid.NewGuid();
        var messageContent = Encoding.UTF8.GetBytes("message-content-" + Guid.NewGuid().ToString("N"));
        var metadataJson = Encoding.UTF8.GetBytes("{\"trace\":\"" + Guid.NewGuid().ToString("N") + "\"}");
        var toolArgs = Encoding.UTF8.GetBytes("{\"prompt\":\"hello\"}");
        var toolResult = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");

        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            writeContext.Conversations.Add(new NodeConversation
            {
                ConversationId = conversationId,
                Title = Encoding.UTF8.GetBytes("Encrypted chat"),
                UserId = "worker-node",
                CreatedAtUtc = 1,
                LastSeenUtc = 2,
                Purged = false
            });

            writeContext.Messages.Add(new NodeMessage
            {
                MessageId = messageId,
                ConversationId = conversationId,
                Sequence = 1,
                Role = "assistant",
                Content = messageContent.ToArray(),
                MetadataJson = metadataJson.ToArray(),
                CreatedAtUtc = 3
            });

            writeContext.ToolEvents.Add(new NodeToolEvent
            {
                ToolCallId = toolCallId,
                ConversationId = conversationId,
                ToolName = "search",
                PlaintextArgs = toolArgs.ToArray(),
                PlaintextResult = toolResult.ToArray(),
                Status = "completed",
                CreatedAtUtc = 4
            });

            await writeContext.SaveChangesAsync();

            var trackedMessage = await writeContext.Messages.SingleAsync();
            AssertBytesEqual(messageContent, trackedMessage.Content, "Tracked message content should be restored to plaintext after SaveChanges.");
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var message = await readContext.Messages.SingleAsync();
        var toolEvent = await readContext.ToolEvents.SingleAsync();

        AssertBytesEqual(messageContent, message.Content, "Message content should decrypt on materialization.");
        AssertBytesEqual(metadataJson, AssertEx.NotNull(message.MetadataJson), "Metadata should decrypt on materialization.");
        AssertBytesEqual(toolArgs, AssertEx.NotNull(toolEvent.PlaintextArgs), "Tool args should decrypt on materialization.");
        AssertBytesEqual(toolResult, AssertEx.NotNull(toolEvent.PlaintextResult), "Tool result should decrypt on materialization.");
    }

    [Test]
    public async Task DatabaseFile_WhenEncryptedPayloadsPersisted_DoesNotContainPlaintext()
    {
        var databasePath = GetDatabasePath("raw-file.sqlite");
        var conversationId = Guid.NewGuid();
        var messageContentText = "raw-file-message-" + Guid.NewGuid().ToString("N");
        var metadataText = "raw-file-metadata-" + Guid.NewGuid().ToString("N");

        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            context.Conversations.Add(new NodeConversation
            {
                ConversationId = conversationId,
                CreatedAtUtc = 1,
                LastSeenUtc = 1
            });

            context.Messages.Add(new NodeMessage
            {
                MessageId = Guid.NewGuid(),
                ConversationId = conversationId,
                Sequence = 1,
                Role = "assistant",
                Content = Encoding.UTF8.GetBytes(messageContentText),
                MetadataJson = Encoding.UTF8.GetBytes(metadataText),
                CreatedAtUtc = 2
            });

            await context.SaveChangesAsync();
        }

        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(databasePath);

        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(messageContentText)), "The SQLite file should not contain plaintext message content.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(metadataText)), "The SQLite file should not contain plaintext metadata.");
    }

    [Test]
    public async Task ConversationTitle_WhenSavedViaEfInterceptor_IsNotPlaintextAtRest()
    {
        var databasePath = GetDatabasePath("title-ef.sqlite");
        var conversationId = Guid.NewGuid();
        const string titleText = "ef-path-title-sentinel";

        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            context.Conversations.Add(new NodeConversation
            {
                ConversationId = conversationId,
                Title = Encoding.UTF8.GetBytes(titleText),
                UserId = "worker-node",
                CreatedAtUtc = 1,
                LastSeenUtc = 2,
                Purged = false
            });

            await context.SaveChangesAsync();
        }

        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(databasePath);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(titleText)),
            "The SQLite file should not contain the plaintext conversation title (EF interceptor path).");

        await using var readContext = CreateContext(databasePath, keyHolder);
        var saved = await readContext.Conversations.SingleAsync();
        AssertEx.NotNull(saved.Title, "Title should not be null after round-trip.");
        AssertEx.True(Encoding.UTF8.GetString(saved.Title!) == titleText,
            "Title should round-trip correctly through EF encrypt/decrypt.");
    }

    [Test]
    public async Task ConversationTitle_WhenSavedViaRawSql_IsNotPlaintextAtRest()
    {
        var databasePath = GetDatabasePath("title-rawsql.sqlite");
        var conversationId = Guid.NewGuid();
        const string titleText = "raw-sql-path-title-sentinel";

        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            await context.Database.ExecuteSqlRawAsync("INSERT INTO conversations (conversation_id, user_id, created_at_utc, last_seen_utc, purged, origin) VALUES ({0}, {1}, {2}, {3}, 0, {4});",
                conversationId, "worker-node", 1L, 2L, "local");

            var encryptedTitle = context.EncryptConversationTitle(titleText, conversationId);

            await context.Database.ExecuteSqlRawAsync("UPDATE conversations SET title = {0} WHERE conversation_id = {1};", encryptedTitle is null ? DBNull.Value : encryptedTitle, conversationId);
        }

        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(databasePath);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(titleText)),
            "The SQLite file should not contain the plaintext conversation title (raw-SQL path).");

        // Use raw ADO.NET to read the BLOB column — EF's SqlQueryRaw cannot materialize scalar byte[] values.
        await using var readContext = CreateContext(databasePath, keyHolder);
        await readContext.Database.OpenConnectionAsync();
        byte[]? raw;
        await using (var cmd = readContext.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT title FROM conversations WHERE conversation_id = $id;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$id";
            p.Value = conversationId;
            cmd.Parameters.Add(p);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            raw = await reader.IsDBNullAsync(0) ? null : (byte[])reader.GetValue(0);
        }

        var decrypted = readContext.DecryptConversationTitle(raw, conversationId);
        AssertEx.True(decrypted == titleText,
            "Title should decrypt correctly via DecryptConversationTitle (raw-SQL path).");
    }

    [Test]
    public async Task GenerationMetadata_WhenSavedViaEfInterceptor_IsCiphertextAtRestAndDecryptsOnRead()
    {
        // AI-drafted generation provenance quotes the operator's brief
        // back, so it belongs on the encrypted surface exactly like the instructions it was drafted from.
        var databasePath = GetDatabasePath("generation-metadata.sqlite");
        var definitionMetadata = Encoding.UTF8.GetBytes("{\"mode\":\"create\",\"userBrief\":\"definition-provenance-sentinel\"}");
        var skillMetadata = Encoding.UTF8.GetBytes("{\"mode\":\"improve\",\"userBrief\":\"skill-provenance-sentinel\"}");

        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            context.AgentDefinitions.Add(new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Drafted builder",
                Instructions = Encoding.UTF8.GetBytes("You are a drafted agent."),
                GenerationMetadataJson = definitionMetadata.ToArray(),
                Version = 1,
                CreatedAtUtc = 1,
                UpdatedAtUtc = 1
            });

            context.AgentSkills.Add(new AgentSkill
            {
                Id = Guid.NewGuid(),
                Name = "drafted-skill",
                Description = Encoding.UTF8.GetBytes("A drafted skill."),
                Body = Encoding.UTF8.GetBytes("# Drafted"),
                GenerationMetadataJson = skillMetadata.ToArray(),
                Version = 1,
                CreatedAtUtc = 1,
                UpdatedAtUtc = 1
            });

            await context.SaveChangesAsync();
        }

        // The context without the materialization interceptor exposes the bytes exactly as SQLite stores them.
        await using (var rawContext = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder))
        {
            var storedDefinitionMetadata = AssertEx.NotNull((await rawContext.AgentDefinitions.SingleAsync()).GenerationMetadataJson, "The definition column should hold a payload.");
            var storedSkillMetadata = AssertEx.NotNull((await rawContext.AgentSkills.SingleAsync()).GenerationMetadataJson, "The skill column should hold a payload.");

            AssertEx.False(storedDefinitionMetadata.SequenceEqual(definitionMetadata), "Definition generation metadata should be encrypted at rest.");
            AssertEx.False(storedSkillMetadata.SequenceEqual(skillMetadata), "Skill generation metadata should be encrypted at rest.");
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var definition = await readContext.AgentDefinitions.SingleAsync();
        var skill = await readContext.AgentSkills.SingleAsync();

        AssertBytesEqual(definitionMetadata, AssertEx.NotNull(definition.GenerationMetadataJson, "Definition provenance should survive the round-trip."),
            "Definition generation metadata should decrypt on materialization.");
        AssertBytesEqual(skillMetadata, AssertEx.NotNull(skill.GenerationMetadataJson, "Skill provenance should survive the round-trip."),
            "Skill generation metadata should decrypt on materialization.");
    }

    [Test]
    public async Task GenerationMetadata_WhenAbsent_RoundTripsNull()
    {
        // The column is null for every row that was not AI-drafted, which is the common case — the optional-property
        // encryption path must leave it alone rather than sealing an empty payload.
        var databasePath = GetDatabasePath("generation-metadata-null.sqlite");

        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            context.AgentDefinitions.Add(new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Hand-written builder",
                Instructions = Encoding.UTF8.GetBytes("You are a hand-written agent."),
                Version = 1,
                CreatedAtUtc = 1,
                UpdatedAtUtc = 1
            });

            context.AgentSkills.Add(new AgentSkill
            {
                Id = Guid.NewGuid(),
                Name = "hand-written-skill",
                Description = Encoding.UTF8.GetBytes("A hand-written skill."),
                Body = Encoding.UTF8.GetBytes("# Hand written"),
                Version = 1,
                CreatedAtUtc = 1,
                UpdatedAtUtc = 1
            });

            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        AssertEx.Null((await readContext.AgentDefinitions.SingleAsync()).GenerationMetadataJson, "A definition without AI provenance should read back null.");
        AssertEx.Null((await readContext.AgentSkills.SingleAsync()).GenerationMetadataJson, "A skill without AI provenance should read back null.");
    }

    [Test]
    public void NodeSqliteKeyHolder_WhenConfigured_DerivesExpectedHkdfKey()
    {
        var operatorSecret = Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray();
        const string nodeName = "worker-node-alpha";
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(operatorSecret)
                            })
                            .Build();

        using var keyHolder = new NodeSqliteKeyHolder(Options.Create(new WorkerNodeOptions
            {
                NodeName = nodeName
            }),
            new NodeOperatorSecretProvider(configuration));

        var actual = keyHolder.Key.ToArray();
        var expected = HkdfSha256(operatorSecret,
            [],
            Encoding.UTF8.GetBytes($"c0re-node-sqlite|v1|{nodeName}"),
            outputLength: 32);

        AssertBytesEqual(expected, actual, "Derived key should match the HKDF-SHA256 reference implementation.");
    }

    [Test]
    public void NodeSqliteKeyHolder_WhenDisposed_ThrowsOnSubsequentAccess()
    {
        var operatorSecret = Enumerable.Range(start: 100, count: 32).Select(static value => (byte)value).ToArray();
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(operatorSecret)
                            })
                            .Build();

        var keyHolder = new NodeSqliteKeyHolder(Options.Create(new WorkerNodeOptions
            {
                NodeName = "worker-node-beta"
            }),
            new NodeOperatorSecretProvider(configuration));

        _ = keyHolder.Key.Span[0];
        keyHolder.Dispose();

        _ = AssertEx.Throws<ObjectDisposedException>(() => _ = keyHolder.Key);
    }

    [Test]
    public void NodeSqliteKeyHolder_WhenSecretMissing_ThrowsHelpfulStartupMessage()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = AssertEx.Throws<InvalidOperationException>(() =>
        {
            _ = new NodeSqliteKeyHolder(Options.Create(new WorkerNodeOptions
                {
                    NodeName = "worker-node-gamma"
                }),
                new NodeOperatorSecretProvider(configuration));
        });

        AssertEx.True(exception.Message.Contains("Parameters:node-sqlite-key", StringComparison.Ordinal));
        AssertEx.True(exception.Message.Contains("dotnet user-secrets set", StringComparison.Ordinal));
        AssertEx.True(exception.Message.Contains("XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj", StringComparison.Ordinal));
    }

    [Test]
    public void NodeJwtKeyProvider_WhenConfigured_DerivesSeparateExpectedHkdfKey()
    {
        var operatorSecret = Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray();
        const string nodeName = "worker-node-alpha";
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(operatorSecret)
                            })
                            .Build();

        using var sqliteKeyHolder = new NodeSqliteKeyHolder(Options.Create(new WorkerNodeOptions
            {
                NodeName = nodeName
            }),
            new NodeOperatorSecretProvider(configuration));
        using var jwtKeyProvider = new NodeJwtKeyProvider(Options.Create(new WorkerNodeOptions
            {
                NodeName = nodeName
            }),
            new NodeOperatorSecretProvider(configuration));

        var actual = jwtKeyProvider.SigningKey.ToArray();
        var expected = HkdfSha256(operatorSecret,
            [],
            Encoding.UTF8.GetBytes($"c0re-node-jwt|v1|{nodeName}"),
            outputLength: 32);

        AssertBytesEqual(expected, actual, "JWT signing key should match the HKDF-SHA256 reference implementation.");
        AssertEx.False(sqliteKeyHolder.Key.Span.SequenceEqual(jwtKeyProvider.SigningKey.Span), "JWT and SQLite keys must use separate HKDF info strings.");
    }

    [Test]
    public void PersistenceAssembly_WhenScanned_DoesNotExposeRatchetArtifacts()
    {
        var prohibitedNames = new[]
        {
            "MasterKeyVersion",
            "RotatedFromEpochVersion"
        };

        var assembly = typeof(NodeChatDbContext).Assembly;
        var discoveredNames = assembly
                              .GetTypes()
                              .SelectMany(type => type
                                                  .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                                  .Select(member => member.Name)
                                                  .Prepend(type.Name))
                              .Where(name => prohibitedNames.Contains(name, StringComparer.Ordinal)
                                             || name.Contains("ratchet", StringComparison.OrdinalIgnoreCase))
                              .Distinct(StringComparer.Ordinal)
                              .ToArray();

        AssertEx.Empty(discoveredNames, "Persistence assembly should not contain ratchet identifiers or forbidden epoch fields.");
    }

    /// <summary>
    ///     Every entity the chat model maps is invisible outside the persistence assembly, so no consumer can bind one.
    /// </summary>
    /// <remarks>
    ///     Pairs with <c>LayerDependencyTests.Persistence_GrantsItsInternalsToNoProductionAssembly</c>: internal types plus
    ///     no production friend assembly is what keeps a caller off the entities. Reflection over the compiled model, so
    ///     it needs no build inside the test run.
    /// </remarks>
    [Test]
    public void ChatModelEntityTypes_WhenScanned_AreAllInvisibleOutsideThePersistenceAssembly()
    {
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        using var context = CreateContext(GetDatabasePath("entity-visibility.sqlite"), keyHolder);
        var persistenceAssembly = typeof(NodeChatDbContext).Assembly;

        var mapped = context.Model.GetEntityTypes()
                            .Select(static entityType => entityType.ClrType)
                            .Distinct()
                            .ToArray();

        AssertEx.True(mapped.Contains(typeof(NodeMessage)),
            "The scan must see NodeMessage, or it lost its subject and would pass vacuously.");
        var foreign = mapped.Where(clrType => clrType.Assembly != persistenceAssembly)
                            .Select(static clrType => clrType.FullName)
                            .Order(StringComparer.Ordinal)
                            .ToArray();
        AssertEx.Empty(foreign,
            "Every chat model entity must be declared in the persistence assembly, where its visibility is enforced; a shared "
            + "or framework type mapped on purpose needs a named exemption here. Mapped from elsewhere: " + string.Join(", ", foreign));
        var visible = mapped.Where(static clrType => clrType.IsVisible).Select(static clrType => clrType.FullName).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Empty(visible,
            "Chat model entities must stay internal: a public entity lets any consumer bind it and bypass the stores. Public entities: "
            + string.Join(", ", visible));
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .AddInterceptors(new NodeEncryptionSaveChangesInterceptor(), new NodeEncryptionMaterializationInterceptor())
                      // Fresh per-test options create a new EF internal service provider; in a FULL-SUITE run the
                      // process-wide count crosses EF's 20-provider threshold and the warning (an error in this solution)
                      // throws. The established repo-wide test pattern is to ignore it on throwaway options.
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        return new NodeChatDbContext(options, keyHolder);
    }

    private string GetDatabasePath(string fileName = "node-chat.sqlite")
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 1)).ToArray();
    }

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string message)
    {
        AssertEx.True(expected.SequenceEqual(actual), message);
    }

    private static bool ContainsSubsequence(byte[] source, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var sourceIndex = 0; sourceIndex <= source.Length - needle.Length; sourceIndex++)
        {
            var matched = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (source[sourceIndex + needleIndex] == needle[needleIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int outputLength)
    {
        var effectiveSalt = salt.Length == 0 ? new byte[32] : salt;
        using var extractHmac = new HMACSHA256(effectiveSalt);
        var pseudorandomKey = extractHmac.ComputeHash(ikm);
        using var expandHmac = new HMACSHA256(pseudorandomKey);
        using var output = new MemoryStream();

        var previousBlock = Array.Empty<byte>();
        byte counter = 1;
        while (output.Length < outputLength)
        {
            var input = new byte[previousBlock.Length + info.Length + 1];
            Buffer.BlockCopy(previousBlock, srcOffset: 0, input, dstOffset: 0, previousBlock.Length);
            Buffer.BlockCopy(info, srcOffset: 0, input, previousBlock.Length, info.Length);
            input[^1] = counter;

            previousBlock = expandHmac.ComputeHash(input);
            output.Write(previousBlock, offset: 0, previousBlock.Length);
            counter++;
        }

        return output.ToArray()[..outputLength];
    }

    private sealed class FixedNodeSqliteKeyHolder : INodeSqliteKeyHolder
    {
        private byte[]? _key;

        public FixedNodeSqliteKeyHolder(byte[] key)
        {
            _key = key;
        }

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
                return _key;
            }
        }

        public void Dispose()
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
