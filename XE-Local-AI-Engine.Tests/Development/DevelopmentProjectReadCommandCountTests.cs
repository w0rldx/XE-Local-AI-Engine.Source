namespace XE_Local_AI_Engine.Tests.Development;

using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The project detail is polled every few seconds while its page is open, so the statements one read costs must not
///     grow with the project's task count. Counted on the real store over a real SQLite file, not on substitutes.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DevelopmentProjectReadCommandCountTests : IDisposable
{
    private static readonly Guid SelectedFolderId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-read-count-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    [Test]
    public async Task GetProject_CostsTheSameStatementsForOneTaskAsForFive()
    {
        var oneTask = await CountGetProjectCommandsAsync(extraTasks: 0);
        var fiveTasks = await CountGetProjectCommandsAsync(extraTasks: 4);

        AssertEx.True(oneTask > 0, "the counter saw the read at all.");
        AssertEx.Equal(oneTask,
            fiveTasks,
            "every task's attempts and artifacts come from one query each, so four more tasks add no round trips.");
    }

    private async Task<int> CountGetProjectCommandsAsync(int extraTasks)
    {
        var counter = new CommandCounter();
        await using var provider = await BuildProviderAsync(counter);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var projectId = Guid.NewGuid();
        _ = await store.CreateProjectAsync(new DevelopmentCreateProjectCommand
        {
            ProjectId = projectId,
            TaskId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Objective = "objective",
            SelectedFolderId = SelectedFolderId,
            RepositoryIdentityHash = "repository-hash",
            BaseBranch = "main",
            Title = "task",
            Requirements = "requirements",
            AcceptanceCriteriaJson = "[\"acceptance\"]"
        });
        for (var index = 0; index < extraTasks; index++)
        {
            _ = await store.CreateTaskAsync(new DevelopmentCreateTaskCommand
            {
                ProjectId = projectId,
                TaskId = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                Title = "task " + index,
                Requirements = "requirements",
                AcceptanceCriteriaJson = "[]",
                MaxReviewRounds = 3
            });
        }

        var graphWorkflows = Substitute.For<IGraphWorkflowStore>();
        graphWorkflows.ListDevelopmentTaskRunsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>()).Returns([]);
        var service = new DevelopmentManagementService(store,
            Substitute.For<IDevelopmentCoordinator>(),
            Substitute.For<IDevelopmentAttemptExecutionSupervisor>(),
            Substitute.For<IDevelopmentArtifactBlobStore>(),
            new DevelopmentManagementServiceTests.UnusedApplyService(),
            Substitute.For<IDevelopmentRepositoryBindingService>(),
            Substitute.For<IActiveCloudChatClientFactory>(),
            new FakeModelTrustResolver(),
            Substitute.For<IDevelopmentCommandProfileDetector>(),
            Substitute.For<IDevelopmentTemplateStore>(),
            graphWorkflows,
            StubNodeRuntimeSettings.Create().Build(),
            TimeProvider.System,
            NullLogger<DevelopmentManagementService>.Instance);

        counter.Reset();
        var project = await service.GetProjectAsync(projectId);

        AssertEx.Equal(extraTasks + 1, project.Tasks.Count);
        return counter.Count;
    }

    private async Task<ServiceProvider> BuildProviderAsync(CommandCounter counter)
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite");
        var services = new ServiceCollection();
        services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        services.AddDbContext<NodeChatDbContext>((serviceProvider, builder) => builder.UseSqlite($"Data Source={databasePath}")
                                                                                      .EnableServiceProviderCaching(false)
                                                                                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                                                                                      .AddInterceptors(serviceProvider.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                                                                                          serviceProvider.GetRequiredService<NodeEncryptionMaterializationInterceptor>(),
                                                                                          counter));
        services.AddScoped<IDevelopmentStore, DevelopmentStore>();
        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                                                                  INSERT INTO selected_folders (id, alias, host_path, mode, created_at_utc)
                                                                  VALUES ({SelectedFolderId}, {"development-read-count"}, {Encoding.UTF8.GetBytes(_root)}, {(int)SelectedFolderMode.Copy}, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()})
                                                                  """);
        return provider;
    }

    /// <summary>Counts every command the context sends, whatever kind of result it reads.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() =>
            Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _count);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
