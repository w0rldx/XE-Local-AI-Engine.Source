namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class DevelopmentPersistenceTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Test]
    public async Task Model_ContainsExactlyFiveDevelopmentTablesAndRequiredUniqueIndexes()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var tables = dbContext.Model.GetEntityTypes()
                              .Select(entity => entity.GetTableName())
                              .Where(name => name?.StartsWith("development_", StringComparison.Ordinal) == true)
                              .Order(StringComparer.Ordinal)
                              .ToArray();

        AssertEx.Equal(expected: 5, tables.Length);
        AssertEx.True(tables.SequenceEqual([
                "development_artifacts",
                "development_attempts",
                "development_events",
                "development_projects",
                "development_tasks"
            ], StringComparer.Ordinal),
            "The Gate 2 schema must contain exactly the five approved Development concepts.");

        var indexNames = dbContext.Model.GetEntityTypes()
                                  .SelectMany(entity => entity.GetIndexes())
                                  .Select(index => index.GetDatabaseName())
                                  .ToHashSet(StringComparer.Ordinal);
        AssertEx.True(indexNames.Contains("ux_development_attempts_one_active_per_task"));
        AssertEx.True(indexNames.Contains("ux_development_events_project_sequence"));
        AssertEx.True(indexNames.Contains("ux_development_events_operation_phase"));
    }

    [Test]
    public async Task Operations_AreIdempotentOrderedAndRejectStaleVersionsAndSecondActiveAttempt()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = DevelopmentTestFixture.CreateSeed();

        var created = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        var replay = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        AssertEx.Equal(created, replay, "The same operation key must reconstruct the original result.");

        var readyOperation = Guid.NewGuid();
        var ready = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                                   readyOperation,
                                                   DevelopmentTaskStatus.Ready,
                                                   ExpectedTaskVersion: 1))
                               .ConfigureAwait(false);
        var readyReplay = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                                         readyOperation,
                                                         DevelopmentTaskStatus.Ready,
                                                         ExpectedTaskVersion: 1))
                                     .ConfigureAwait(false);
        AssertEx.Equal(ready, readyReplay);

        await AssertEx.ThrowsAsync<DevelopmentConcurrencyException>(() => store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                                                                                            Guid.NewGuid(),
                                                                                                            DevelopmentTaskStatus.InProgress,
                                                                                                            ExpectedTaskVersion: 1)))
                      .ConfigureAwait(false);

        var firstAttempt = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                                        Guid.NewGuid(),
                                                        Guid.NewGuid(),
                                                        DevelopmentAttemptRole.Coder,
                                                        "local-model",
                                                        "local",
                                                        ExpectedTaskVersion: 2))
                                      .ConfigureAwait(false);
        AssertEx.Equal(DevelopmentAttemptStatus.Running.ToString(), firstAttempt.Status);

        await AssertEx.ThrowsAsync<DevelopmentConcurrencyException>(() => store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                                                                                      Guid.NewGuid(),
                                                                                                      Guid.NewGuid(),
                                                                                                      DevelopmentAttemptRole.Coder,
                                                                                                      "local-model",
                                                                                                      "local",
                                                                                                      ExpectedTaskVersion: 3)))
                      .ConfigureAwait(false);

        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 3, events.Count);
        AssertEx.True(events.Select(item => item.Sequence).SequenceEqual([1L, 2L, 3L]));
    }
}

public sealed class DevelopmentStartupReconcilerTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Test]
    public async Task ReconcileRunningAttempts_IsExactlyOnceAndLeavesOrderedInterruptionEvent()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId, Guid.NewGuid(), DevelopmentTaskStatus.Ready, 1)).ConfigureAwait(false);
        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                                attemptId,
                                                Guid.NewGuid(),
                                                DevelopmentAttemptRole.Coder,
                                                "local-model",
                                                "local",
                                                ExpectedTaskVersion: 2))
                       .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, await store.ReconcileRunningAttemptsAsync("restart").ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await store.ReconcileRunningAttemptsAsync("restart").ConfigureAwait(false));

        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == "AttemptInterrupted"));
        AssertEx.Equal(attemptId, events.Single(item => item.EventType == "AttemptInterrupted").AttemptId);
    }

    [Test]
    public async Task ReconcileRunningAttempts_ConcurrentCallsProduceOneTransitionAndOneEvent()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedStore = seedScope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
            var seed = DevelopmentTestFixture.CreateSeed();
            _ = await seedStore.CreateProjectAsync(seed).ConfigureAwait(false);
            _ = await seedStore.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId, Guid.NewGuid(), DevelopmentTaskStatus.Ready, 1)).ConfigureAwait(false);
            _ = await seedStore.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                                    Guid.NewGuid(),
                                                    Guid.NewGuid(),
                                                    DevelopmentAttemptRole.Coder,
                                                    "local-model",
                                                    "local",
                                                    ExpectedTaskVersion: 2))
                               .ConfigureAwait(false);

            await using var firstScope = provider.CreateAsyncScope();
            await using var secondScope = provider.CreateAsyncScope();
            var first = firstScope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ReconcileRunningAttemptsAsync("restart");
            var second = secondScope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ReconcileRunningAttemptsAsync("restart");
            var results = await Task.WhenAll(first, second).ConfigureAwait(false);

            AssertEx.Equal(expected: 1, results.Sum());
            var events = await seedStore.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
            AssertEx.Equal(expected: 1, events.Count(item => item.EventType == "AttemptInterrupted"));
        }
    }
}

public sealed class ManagedDevelopmentArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-artifacts-" + Guid.NewGuid().ToString("N"));
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task WriteReadAndTamper_UsesOpaqueImmutableHashVerifiedArtifacts()
    {
        var store = new ManagedDevelopmentArtifactBlobStore(new TestDataDirectory(_root),
            _keyHolder,
            Options.Create(new DevelopmentOptions { Enabled = true, MaxArtifactBytes = 1024 }));
        var projectId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        ReadOnlyMemory<byte> content = "bounded artifact"u8.ToArray();

        var written = await store.WriteAsync(projectId, artifactId, content).ConfigureAwait(false);
        AssertEx.False(Path.IsPathRooted(written.OpaqueReference));
        AssertEx.False(written.OpaqueReference.Contains("..", StringComparison.Ordinal));

        var read = await store.ReadAsync(projectId, artifactId, written.ContentHash, written.ByteCount).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentArtifactReadStatus.Found, read.Status);
        AssertEx.True(read.Content.Span.SequenceEqual(content.Span));

        var path = Path.Combine(_root, "development", "artifacts", projectId.ToString("N"), artifactId.ToString("N") + ".blob");
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        var tampered = await store.ReadAsync(projectId, artifactId, written.ContentHash, written.ByteCount).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentArtifactReadStatus.Tampered, tampered.Status);
    }

    [Test]
    public async Task OversizedWrite_FailsBeforeFinalOrTemporaryArtifactExists()
    {
        var store = new ManagedDevelopmentArtifactBlobStore(new TestDataDirectory(_root),
            _keyHolder,
            Options.Create(new DevelopmentOptions { Enabled = true, MaxArtifactBytes = 4 }));
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new byte[5]))
                      .ConfigureAwait(false);
        AssertEx.False(Directory.Exists(Path.Combine(_root, "development")));
    }

    [Test]
    public async Task AttachArtifact_RejectsCallerProvidedManagedReference()
    {
        var fixture = new DevelopmentTestFixture();
        try
        {
            await using var provider = await fixture.BuildProviderAsync().ConfigureAwait(false);
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
            var seed = DevelopmentTestFixture.CreateSeed();
            _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);

            await AssertEx.ThrowsAsync<ArgumentException>(() => store.AttachArtifactAsync(new DevelopmentAttachArtifactCommand(Guid.NewGuid(),
                                                                                                      seed.ProjectId,
                                                                                                      seed.TaskId,
                                                                                                      AttemptId: null,
                                                                                                      Guid.NewGuid(),
                                                                                                      DevelopmentArtifactKind.Patch,
                                                                                                      SchemaVersion: 1,
                                                                                                      ContentHash: "hash",
                                                                                                      ByteCount: 1,
                                                                                                      ManagedReference: "../../caller/path")))
                          .ConfigureAwait(false);
        }
        finally
        {
            fixture.Dispose();
        }
    }
}

public sealed class DevelopmentApplyRecoveryTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Test]
    public async Task CrashAfterHostMutation_SameKeyFinalizesWithoutApplyingTwice()
    {
        var port = new CrashAfterMutationApplyPort();
        await using var provider = await _fixture.BuildProviderAsync(port).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = new DevelopmentApprovedApplySubject(seed.ProjectId,
            seed.TaskId,
            version,
            "base",
            "patch",
            "manifest",
            "result",
            "patch-ref",
            "manifest-ref");

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(operationId, subject)).ConfigureAwait(false);
        var completed = await coordinator.ApplyAsync(operationId, subject).ConfigureAwait(false);

        AssertEx.Equal(DevelopmentOperationPhases.ApplyCompleted, completed.Phase);
        AssertEx.Equal(expected: 1, port.ApplyCalls);
        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyStarted));
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyCompleted));
    }

    [Test]
    public async Task CrashAfterApplyStartedBeforeHostMutation_RetryAppliesOnceAndCompletes()
    {
        var port = new CrashBeforeHostMutationApplyPort();
        await using var provider = await _fixture.BuildProviderAsync(port).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = CreateSubject(seed, version);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(operationId, subject)).ConfigureAwait(false);
        var completed = await coordinator.ApplyAsync(operationId, subject).ConfigureAwait(false);

        AssertEx.Equal(DevelopmentOperationPhases.ApplyCompleted, completed.Phase);
        AssertEx.Equal(expected: 1, port.ApplyCalls);
        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyStarted));
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyCompleted));
    }

    [Test]
    public async Task CompletedApply_ResponseReplayDoesNotInspectOrMutateHostAgain()
    {
        var port = new CountingApplyPort();
        await using var provider = await _fixture.BuildProviderAsync(port).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = CreateSubject(seed, version);

        var completed = await coordinator.ApplyAsync(operationId, subject).ConfigureAwait(false);
        var replay = await coordinator.ApplyAsync(operationId, subject).ConfigureAwait(false);

        AssertEx.Equal(completed, replay);
        AssertEx.Equal(expected: 1, port.InspectCalls);
        AssertEx.Equal(expected: 1, port.ApplyCalls);
        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyStarted));
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyCompleted));
    }

    [Test]
    public async Task AmbiguousHostState_BlocksIdempotentlyWithoutApplying()
    {
        var port = new AmbiguousApplyPort();
        await using var provider = await _fixture.BuildProviderAsync(port).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = CreateSubject(seed, version);

        var blocked = await coordinator.ApplyAsync(operationId, subject).ConfigureAwait(false);
        var replay = await coordinator.ApplyAsync(operationId, subject).ConfigureAwait(false);

        AssertEx.Equal(blocked, replay);
        AssertEx.Equal(DevelopmentOperationPhases.ApplyBlocked, blocked.Phase);
        AssertEx.Equal(expected: 1, port.InspectCalls);
        AssertEx.Equal(expected: 0, port.ApplyCalls);
        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyStarted));
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyBlocked));
        AssertEx.Equal(expected: 0, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyCompleted));
    }

    [Test]
    public async Task GenericTransition_CannotBypassExplicitApplyCompletion()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                                                                                                      Guid.NewGuid(),
                                                                                                                      DevelopmentTaskStatus.Completed,
                                                                                                                      version)))
                      .ConfigureAwait(false);
    }

    private static DevelopmentApprovedApplySubject CreateSubject(DevelopmentCreateProjectCommand seed, long version)
        => new(seed.ProjectId, seed.TaskId, version, "base", "patch", "manifest", "result", "patch-ref", "manifest-ref");

    private static async Task<(DevelopmentCreateProjectCommand Seed, long Version)> SeedAwaitingApplyAsync(IDevelopmentStore store)
    {
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        var version = 1L;
        foreach (var status in new[]
                 {
                     DevelopmentTaskStatus.Ready,
                     DevelopmentTaskStatus.InProgress,
                     DevelopmentTaskStatus.Validation,
                     DevelopmentTaskStatus.InReview,
                     DevelopmentTaskStatus.AwaitingApply
                 })
        {
            var result = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId, Guid.NewGuid(), status, version)).ConfigureAwait(false);
            version = result.Version;
        }

        return (seed, version);
    }
}

public sealed class DevelopmentMigrationTests : IDisposable
{
    private const string PreDevelopmentMigrationId = "20260718143054_AddAgentExecutionLogProvider";

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-migration-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Migration_AppliesExactlyFiveDevelopmentTablesAndRequiredIndexesThenRollsBack()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "development.sqlite");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreDevelopmentMigrationId).ConfigureAwait(false);
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            AssertEx.True((await NamesAsync(connection, "table", "development_%").ConfigureAwait(false)).SetEquals([
                "development_artifacts",
                "development_attempts",
                "development_events",
                "development_projects",
                "development_tasks"
            ]));
            var indexes = await NamesAsync(connection, "index", "ux_development_%").ConfigureAwait(false);
            AssertEx.True(indexes.Contains("ux_development_attempts_one_active_per_task"));
            AssertEx.True(indexes.Contains("ux_development_events_project_sequence"));
            AssertEx.True(indexes.Contains("ux_development_events_operation_phase"));
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreDevelopmentMigrationId).ConfigureAwait(false);
        }

        await using var rolledBack = new SqliteConnection($"Data Source={databasePath}");
        await rolledBack.OpenAsync().ConfigureAwait(false);
        AssertEx.Empty(await NamesAsync(rolledBack, "table", "development_%").ConfigureAwait(false));
    }

    private static async Task<HashSet<string>> NamesAsync(SqliteConnection connection, string type, string pattern)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name LIKE $pattern ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$pattern", pattern);
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}

public sealed class DevelopmentServiceRegistrationTests
{
    [Test]
    public void AddNodeDevelopment_DisabledRegistersNoRuntimeServices()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Development:Enabled"] = "false" }).Build();
        builder.AddNodeDevelopment(configuration);
        using var provider = builder.Services.BuildServiceProvider();
        AssertEx.Null(provider.GetService<IDevelopmentCoordinator>());
        AssertEx.Null(provider.GetService<IDevelopmentArtifactBlobStore>());
    }

    [Test]
    public void AddNodeDevelopment_EnabledRegistersExactlyFiveEntityFoundationServices()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Development:Enabled"] = "true",
            ["Development:MaxArtifactBytes"] = "1024"
        }).Build();
        builder.AddNodeDevelopment(configuration);
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentStore)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentCoordinator)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentArtifactBlobStore)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                                        && descriptor.ImplementationType == typeof(DevelopmentStartupReconciler)));
    }
}

internal sealed class DevelopmentTestFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-gate2-" + Guid.NewGuid().ToString("N"));
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();

    public async Task<ServiceProvider> BuildProviderAsync(IDevelopmentHostApplyPort? port = null)
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite");
        var services = new ServiceCollection();
        services.AddSingleton<INodeSqliteKeyHolder>(_keyHolder);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        services.AddDbContext<NodeChatDbContext>((provider, options) => options.UseSqlite($"Data Source={databasePath}")
                                                                         .EnableServiceProviderCaching(false)
                                                                         .AddInterceptors(provider.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                                                                             provider.GetRequiredService<NodeEncryptionMaterializationInterceptor>()));
        services.AddScoped<IDevelopmentStore, DevelopmentStore>();
        services.AddScoped<IDevelopmentHostApplyPort>(_ => port ?? new TestApplyPort());
        services.AddScoped<IDevelopmentCoordinator, DevelopmentCoordinator>();
        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.EnsureCreatedAsync().ConfigureAwait(false);
        return provider;
    }

    public static DevelopmentCreateProjectCommand CreateSeed()
        => new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "objective",
            "repository-hash",
            "main",
            "task",
            "requirements",
            "[\"acceptance\"]");

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

internal sealed class TestDataDirectory(string root) : INodeDataDirectory
{
    public string Root { get; } = root;
}

internal class TestApplyPort : IDevelopmentHostApplyPort
{
    public virtual Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
        => Task.FromResult(DevelopmentHostApplyState.UnappliedBaseUnchanged);

    public virtual Task ApplyAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class CrashAfterMutationApplyPort : TestApplyPort
{
    private bool _applied;

    public int ApplyCalls { get; private set; }

    public override Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
        => Task.FromResult(_applied ? DevelopmentHostApplyState.ExactApprovedResultPresent : DevelopmentHostApplyState.UnappliedBaseUnchanged);

    public override Task ApplyAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        _applied = true;
        throw new InvalidOperationException("Simulated crash after host mutation.");
    }
}

internal sealed class CrashBeforeHostMutationApplyPort : TestApplyPort
{
    private bool _crashed;

    public int ApplyCalls { get; private set; }

    public override Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
    {
        if (!_crashed)
        {
            _crashed = true;
            throw new InvalidOperationException("Simulated crash after ApplyStarted and before host mutation.");
        }

        return Task.FromResult(DevelopmentHostApplyState.UnappliedBaseUnchanged);
    }

    public override Task ApplyAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class CountingApplyPort : TestApplyPort
{
    public int InspectCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public override Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
    {
        InspectCalls++;
        return Task.FromResult(DevelopmentHostApplyState.UnappliedBaseUnchanged);
    }

    public override Task ApplyAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class AmbiguousApplyPort : TestApplyPort
{
    public int InspectCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public override Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
    {
        InspectCalls++;
        return Task.FromResult(DevelopmentHostApplyState.Ambiguous);
    }

    public override Task ApplyAsync(DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        return Task.CompletedTask;
    }
}
