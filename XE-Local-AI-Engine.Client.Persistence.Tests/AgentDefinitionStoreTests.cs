namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AgentDefinitionStoreTests : IDisposable
{
    private const string Instructions = "You are a careful engineering agent. Follow the repository conventions exactly.";
    private const string Description = "Pairs on backend refactors with conservative edits.";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddAsync_ThenReadBackInNewContext_DecryptsInstructionsAndDescription()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid definitionId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new AgentDefinitionStore(writeContext, TimeProvider.System);
            var added = await store.AddAsync(CreateInput());

            AssertEx.Equal("Builder", added.Name);
            AssertEx.Equal(Instructions, added.Instructions);
            AssertEx.Equal(Description, added.Description);
            AssertEx.Equal(expected: 1, added.Version);
            AssertEx.True(added.Id != Guid.Empty, "Add should assign a definition id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp a creation time.");
            AssertEx.Equal(added.CreatedAtUtc, added.UpdatedAtUtc);
            definitionId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(definitionId), "Definition should be found by id.");
        AssertEx.Equal(Instructions, byId.Instructions);
        AssertEx.Equal(Description, byId.Description);

        var list = await readStore.ListAsync();
        AssertEx.Equal(expected: 1, list.Count);

        var unknown = await readStore.GetByIdAsync(Guid.NewGuid());
        AssertEx.Null(unknown, "Unknown id should return null.");
    }

    [Test]
    public async Task AddAsync_WithNullDescription_RoundTripsNull()
    {
        var databasePath = GetDatabasePath("null-description.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid definitionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);
            var added = await store.AddAsync(CreateInput() with
            {
                Description = null
            });
            definitionId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(definitionId), "Definition should be found by id.");
        AssertEx.Null(record.Description, "A null description should round-trip as null.");
        AssertEx.Equal(Instructions, record.Instructions);
    }

    [Test]
    public async Task AddAsync_WithNonDefaultConfig_RoundTripsKindReasoningAndToolConfig()
    {
        var databasePath = GetDatabasePath("config.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid definitionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);
            var input = CreateInput() with
            {
                Kind = AgentDefinitionKind.Orchestrator,
                ReasoningEffort = "high",
                ModelProfile = "qwen3:8b",
                AllowedToolNames = new[]
                {
                    "run_in_agent_home",
                    "export_patch"
                },
                ToolApprovals = new Dictionary<string, bool>
                {
                    ["run_in_agent_home"] = true,
                    ["export_patch"] = false
                },
                OrchestrationTopologyJson = "{\"nodes\":[]}"
            };
            var added = await store.AddAsync(input);
            definitionId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(definitionId), "Definition should be found by id.");
        AssertEx.Equal(AgentDefinitionKind.Orchestrator, record.Kind);
        AssertEx.Equal("high", record.ReasoningEffort);
        AssertEx.Equal("qwen3:8b", record.ModelProfile);
        AssertEx.Equal("{\"nodes\":[]}", record.OrchestrationTopologyJson);
        AssertEx.Equal(expected: 2, record.AllowedToolNames.Count);
        AssertEx.True(record.AllowedToolNames.Contains("run_in_agent_home"), "Tool list should round-trip.");
        AssertEx.True(record.ToolApprovals["run_in_agent_home"], "Approval flag should round-trip as true.");
        AssertEx.False(record.ToolApprovals["export_patch"], "Approval flag should round-trip as false.");
    }

    [Test]
    public async Task DatabaseFile_AfterAdd_DoesNotContainPlaintextInstructionsOrDescription()
    {
        var databasePath = GetDatabasePath("ciphertext.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var instructions = "SECRET-PROMPT-" + Guid.NewGuid().ToString("N");
        var description = "SECRET-DESC-" + Guid.NewGuid().ToString("N");

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);
            _ = await store.AddAsync(CreateInput() with
            {
                Instructions = instructions,
                Description = description
            });
        }

        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(databasePath);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(instructions)),
            "The SQLite file should not contain the plaintext instructions.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(description)),
            "The SQLite file should not contain the plaintext description.");
    }

    [Test]
    public async Task UpdateAsync_WhenConfigFieldChanges_BumpsVersionAndUpdatedAt()
    {
        var databasePath = GetDatabasePath("version-bump.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentDefinitionStore(context, clock);

        var added = await store.AddAsync(CreateInput());
        AssertEx.Equal(expected: 1, added.Version);

        clock.Advance(50);
        var updated = AssertEx.NotNull(await store.UpdateAsync(added.Id, CreateInput() with
            {
                Instructions = "A different system prompt."
            }),
            "Update should find the definition.");

        AssertEx.Equal(expected: 2, updated.Version);
        AssertEx.True(updated.UpdatedAtUtc > added.UpdatedAtUtc, "A config change should advance UpdatedAtUtc.");
    }

    [Test]
    public async Task UpdateAsync_WhenOnlyNameOrDescriptionChanges_DoesNotBumpVersion()
    {
        var databasePath = GetDatabasePath("no-bump.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(2_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentDefinitionStore(context, clock);

        var added = await store.AddAsync(CreateInput());

        clock.Advance(25);
        var updated = AssertEx.NotNull(await store.UpdateAsync(added.Id, CreateInput() with
            {
                Name = "Renamed",
                Description = "New description only."
            }),
            "Update should find the definition.");

        AssertEx.Equal("Renamed", updated.Name);
        AssertEx.Equal("New description only.", updated.Description);
        AssertEx.Equal(expected: 1, updated.Version);
        AssertEx.True(updated.UpdatedAtUtc > added.UpdatedAtUtc, "A name/description edit should still advance UpdatedAtUtc.");
    }

    [Test]
    public async Task UpdateAsync_WhenToolApprovalsReorderedButUnchanged_DoesNotBumpVersion()
    {
        var databasePath = GetDatabasePath("approvals-reorder.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(3_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentDefinitionStore(context, clock);

        var allowed = new[]
        {
            "alpha",
            "bravo",
            "charlie"
        };
        var added = await store.AddAsync(CreateInput() with
        {
            AllowedToolNames = allowed,
            ToolApprovals = new Dictionary<string, bool>
            {
                ["alpha"] = true,
                ["bravo"] = false,
                ["charlie"] = true
            }
        });
        AssertEx.Equal(expected: 1, added.Version);

        // Same approvals, different key insertion order — must be treated as no config change.
        clock.Advance(10);
        var reordered = AssertEx.NotNull(await store.UpdateAsync(added.Id, CreateInput() with
            {
                AllowedToolNames = allowed,
                ToolApprovals = new Dictionary<string, bool>
                {
                    ["charlie"] = true,
                    ["alpha"] = true,
                    ["bravo"] = false
                }
            }),
            "Update should find the definition.");

        AssertEx.Equal(expected: 1, reordered.Version);
        AssertEx.True(reordered.UpdatedAtUtc > added.UpdatedAtUtc, "An approvals reorder should still advance UpdatedAtUtc.");

        // Flipping an actual approval value is a real config change and must bump the version.
        clock.Advance(10);
        var flipped = AssertEx.NotNull(await store.UpdateAsync(added.Id, CreateInput() with
            {
                AllowedToolNames = allowed,
                ToolApprovals = new Dictionary<string, bool>
                {
                    ["alpha"] = false,
                    ["bravo"] = false,
                    ["charlie"] = true
                }
            }),
            "Update should find the definition.");

        AssertEx.Equal(expected: 2, flipped.Version);
    }

    [Test]
    public async Task UpdateAsync_WhenIdMissing_ReturnsNull()
    {
        var databasePath = GetDatabasePath("update-missing.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentDefinitionStore(context, TimeProvider.System);

        var result = await store.UpdateAsync(Guid.NewGuid(), CreateInput());
        AssertEx.Null(result, "Updating an unknown id should return null.");
    }

    [Test]
    public async Task DeleteAsync_RemovesRow()
    {
        var databasePath = GetDatabasePath("delete.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentDefinitionStore(context, TimeProvider.System);

        var added = await store.AddAsync(CreateInput());

        AssertEx.True(await store.DeleteAsync(added.Id), "Delete should report a removed row.");
        AssertEx.Null(await store.GetByIdAsync(added.Id), "Deleted definition should no longer be found.");
        AssertEx.False(await store.DeleteAsync(added.Id), "Deleting a missing id should report no removal.");
    }

    [Test]
    public async Task GetBySeedSlugAsync_ReturnsSeededRow()
    {
        const string seedSlug = "default-assistant";
        var databasePath = GetDatabasePath("seed-slug.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid seededId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);
            // A manual row plus the seeded row: the projection must select the seeded row by slug, never the manual one.
            _ = await store.AddAsync(CreateInput() with
            {
                Name = "Manual"
            });
            var seeded = await store.AddSeededAsync(CreateInput() with
            {
                Name = "Default Assistant"
            }, seedSlug);
            seededId = seeded.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);

        var found = AssertEx.NotNull(await readStore.GetBySeedSlugAsync(seedSlug), "The seeded row should be found by slug.");
        AssertEx.Equal(seededId, found.Id);
        AssertEx.Equal(AgentDefinitionSource.Seeded, found.Source);
        AssertEx.Equal(seedSlug, found.SeedSlug);
        AssertEx.Equal("Default Assistant", found.Name);
        AssertEx.Equal(Instructions, found.Instructions);

        var missing = await readStore.GetBySeedSlugAsync("not-a-known-slug");
        AssertEx.Null(missing, "An unknown slug should return null.");
    }

    [Test]
    public async Task AddAsync_DefaultAllowedSkillIds_RoundTripsAsEmpty()
    {
        var databasePath = GetDatabasePath("skills-default.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid definitionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);
            // CreateInput() supplies no AllowedSkillIds, so the picklist must default to an empty (never null) list.
            var added = await store.AddAsync(CreateInput());
            AssertEx.NotNull(added.AllowedSkillIds, "AllowedSkillIds should never be null on a stored record.");
            AssertEx.Empty(added.AllowedSkillIds!, "A definition with no assigned skills should round-trip an empty list.");
            definitionId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(definitionId), "Definition should be found by id.");
        AssertEx.Empty(record.AllowedSkillIds!, "The empty picklist should survive a read in a new context.");
    }

    [Test]
    public async Task AddAndUpdate_AllowedSkillIds_RoundTripsAndChangeBumpsVersion()
    {
        var databasePath = GetDatabasePath("skills-assigned.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(5_000);
        var firstSkill = Guid.NewGuid();
        var secondSkill = Guid.NewGuid();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentDefinitionStore(context, clock);

        var added = await store.AddAsync(CreateInput() with
        {
            AllowedSkillIds = new[]
            {
                firstSkill,
                secondSkill
            }
        });
        AssertEx.Equal(expected: 2, added.AllowedSkillIds!.Count);
        AssertEx.Equal(firstSkill, added.AllowedSkillIds[0]);
        AssertEx.Equal(secondSkill, added.AllowedSkillIds[1]);
        AssertEx.Equal(expected: 1, added.Version);

        // Changing the assigned skill set is config-affecting (same class as the tool list) and must bump Version.
        clock.Advance(10);
        var updated = AssertEx.NotNull(await store.UpdateAsync(added.Id, CreateInput() with
            {
                AllowedSkillIds = new[]
                {
                    firstSkill
                }
            }),
            "Update should find the definition.");
        AssertEx.Equal(expected: 1, updated.AllowedSkillIds!.Count);
        AssertEx.Equal(firstSkill, updated.AllowedSkillIds[0]);
        AssertEx.Equal(expected: 2, updated.Version);

        // Re-applying the same skill set is not a config change and must not bump Version again.
        clock.Advance(10);
        var unchanged = AssertEx.NotNull(await store.UpdateAsync(added.Id, CreateInput() with
            {
                AllowedSkillIds = new[]
                {
                    firstSkill
                }
            }),
            "Update should find the definition.");
        AssertEx.Equal(expected: 2, unchanged.Version);
    }

    [Test]
    public async Task GetByIdAsync_WhenInstructionsTampered_FailsAuthenticatedDecryption()
    {
        var databasePath = GetDatabasePath("tamper.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid definitionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);
            var added = await store.AddAsync(CreateInput());
            definitionId = added.Id;
        }

        await TamperInstructionsAsync(databasePath);

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);

        _ = AssertEx.Throws<CryptographicException>(() => readStore.GetByIdAsync(definitionId).GetAwaiter().GetResult(),
            "Tampered instructions ciphertext should fail authenticated decryption.");
    }

    private static AgentDefinitionInput CreateInput()
    {
        return new AgentDefinitionInput("Builder",
            Description,
            Instructions,
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null);
    }

    private static async Task TamperInstructionsAsync(string databasePath)
    {
        // The test database holds exactly one definition, so the corruption targets that single row.
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        byte[] blob;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT instructions FROM agent_definitions LIMIT 1;";
            blob = (byte[])(await read.ExecuteScalarAsync())!;
        }

        // Flip a byte of the trailing AES-GCM authentication tag so authenticated decryption must reject it.
        blob[^1] ^= 0xFF;

        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE agent_definitions SET instructions = $blob;";
        write.Parameters.AddWithValue("$blob", blob);
        _ = await write.ExecuteNonQueryAsync();
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        return AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 1)).ToArray();
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

    private sealed class MutableTimeProvider(long initialMilliseconds) : TimeProvider
    {
        private long _milliseconds = initialMilliseconds;

        public void Advance(long milliseconds)
        {
            _milliseconds += milliseconds;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(_milliseconds);
        }
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
