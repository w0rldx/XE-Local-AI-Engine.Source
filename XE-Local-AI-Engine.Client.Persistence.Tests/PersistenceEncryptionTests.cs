namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Persistence;

public sealed class PersistenceEncryptionTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
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
                Title = "Encrypted chat",
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

        var fileBytes = await File.ReadAllBytesAsync(databasePath);

        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(messageContentText)), "The SQLite file should not contain plaintext message content.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(metadataText)), "The SQLite file should not contain plaintext metadata.");
    }

    [Test]
    public void NodeSqliteKeyHolder_WhenConfigured_DerivesExpectedHkdfKey()
    {
        var operatorSecret = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
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
            32);

        AssertBytesEqual(expected, actual, "Derived key should match the HKDF-SHA256 reference implementation.");
    }

    [Test]
    public void NodeSqliteKeyHolder_WhenDisposed_ThrowsOnSubsequentAccess()
    {
        var operatorSecret = Enumerable.Range(100, 32).Select(static value => (byte)value).ToArray();
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
        var operatorSecret = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
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
            32);

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

    [Test]
    public async Task NegativeFenceProbe_WhenBuilt_FailsBecausePersistenceEntitiesAreInternal()
    {
        var projectPath = Path.Combine(GetProjectDirectory(), "NegativeFence", "XE-Local-AI-Engine.Client.Persistence.NegativeFence.csproj");
        var startInfo = new ProcessStartInfo("dotnet", $"build \"{projectPath}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        using var process = AssertEx.NotNull(Process.Start(startInfo), "Expected negative fence build process to start.");

        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var combinedOutput = standardOutput + Environment.NewLine + standardError;

        AssertEx.False(process.ExitCode == 0, "Negative fence probe must fail to compile.");
        AssertEx.True(combinedOutput.Contains("CS0122", StringComparison.Ordinal)
                      || combinedOutput.Contains("inaccessible due to its protection level", StringComparison.OrdinalIgnoreCase),
            "Negative fence build output should show an accessibility failure.");
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .AddInterceptors(new NodeEncryptionSaveChangesInterceptor(), new NodeEncryptionMaterializationInterceptor())
                      .Options;

        return new NodeChatDbContext(options, keyHolder);
    }

    private string GetDatabasePath(string fileName = "node-chat.sqlite")
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static string GetProjectDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "XE-Local-AI-Engine.Client.Persistence.Tests.csproj");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the persistence test project directory.");
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(0, 32).Select(static value => (byte)(value + 1)).ToArray();
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
            Buffer.BlockCopy(previousBlock, 0, input, 0, previousBlock.Length);
            Buffer.BlockCopy(info, 0, input, previousBlock.Length, info.Length);
            input[^1] = counter;

            previousBlock = expandHmac.ComputeHash(input);
            output.Write(previousBlock, 0, previousBlock.Length);
            counter++;
        }

        return output.ToArray()[..outputLength];
    }

    private sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
    {
        private byte[]? _key = key;

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
