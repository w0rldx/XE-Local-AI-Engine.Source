namespace XE_Local_AI_Engine.Tests.Memory;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Tests for the bounded background memory extraction pipeline: <see cref="MemoryExtractionDispatcher" /> TRY-enqueues
///     jobs onto a bounded queue (never blocking the caller; a full queue drops the newest), and
///     <see cref="MemoryExtractionWorker" /> drains that queue under a concurrency gate, writing a metadata-only
///     <c>AgentExecutionLog</c> row (no message content) on its OWN scope/DbContext, running the extraction service, and
///     never surfacing a failure. Work is asynchronous, so assertions poll for the background work to land.
/// </summary>
public sealed class MemoryExtractionDispatcherTests : IDisposable
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
    public async Task Dispatch_OnCompletedRun_WritesMetadataOnlyExecutionLog()
    {
        var agentId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await using var provider = await BuildProviderAsync("exec-log.sqlite").ConfigureAwait(false);
        await using var pipeline = await Pipeline.StartAsync(provider).ConfigureAwait(false);

        var telemetry = new MemoryExtractionDispatchContext(agentId,
            conversationId,
            messageId,
            "qwen3:8b",
            "config-hash-abc",
            LatencyMs: 1234,
            Success: true,
            PromptTokens: 10,
            CompletionTokens: 3,
            ErrorClass: null);

        pipeline.Dispatcher.Dispatch(telemetry, Run(agentId, conversationId, messageId));

        var row = await PollForLogAsync(provider, agentId).ConfigureAwait(false);
        AssertEx.Equal(conversationId, row.ConversationId);
        AssertEx.Equal(messageId, row.MessageId);
        AssertEx.Equal("qwen3:8b", row.ModelName);
        AssertEx.Equal("config-hash-abc", row.ConfigHash);
        AssertEx.Equal(expected: 1234, row.LatencyMs);
        AssertEx.True(row.Success);
        AssertEx.Equal(expected: 10, row.PromptTokens);
        AssertEx.Equal(expected: 3, row.CompletionTokens);
        AssertEx.Null(row.ErrorClass);
    }

    [Test]
    public async Task Dispatch_WhenTokensAbsent_DegradesToNullGracefully()
    {
        var agentId = Guid.NewGuid();
        await using var provider = await BuildProviderAsync("exec-log-null-tokens.sqlite").ConfigureAwait(false);
        await using var pipeline = await Pipeline.StartAsync(provider).ConfigureAwait(false);

        // A GGUF model may report no usage — PromptTokens/CompletionTokens null must persist cleanly (nullable columns).
        var telemetry = new MemoryExtractionDispatchContext(agentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "gguf-model",
            "config-hash",
            LatencyMs: 500,
            Success: false,
            PromptTokens: null,
            CompletionTokens: null,
            "Unexpected");

        pipeline.Dispatcher.Dispatch(telemetry, Run(agentId, telemetry.ConversationId, telemetry.MessageId, failed: true));

        var row = await PollForLogAsync(provider, agentId).ConfigureAwait(false);
        AssertEx.Null(row.PromptTokens);
        AssertEx.Null(row.CompletionTokens);
        AssertEx.False(row.Success);
        AssertEx.Equal("Unexpected", row.ErrorClass ?? string.Empty);
    }

    [Test]
    public async Task Dispatch_WhenExtractionThrows_DoesNotSurfaceAndStillRunsTheJob()
    {
        var agentId = Guid.NewGuid();
        var extraction = Substitute.For<IMemoryExtractionService>();
        // A failure inside the background extraction must be swallowed by the worker's catch-all — it must never surface
        // to the (fire-and-forget) caller nor escape as an unobserved task exception that affects the chat path.
        extraction.When(service => service.ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>()))
                  .Do(_ => throw new InvalidOperationException("extraction blew up"));
        await using var provider = await BuildProviderAsync("exec-log-throw.sqlite", extraction).ConfigureAwait(false);
        await using var pipeline = await Pipeline.StartAsync(provider).ConfigureAwait(false);

        var telemetry = Telemetry(agentId);

        // Dispatch is void/fire-and-forget: it must return normally even though the background extraction will throw.
        pipeline.Dispatcher.Dispatch(telemetry, Run(agentId, telemetry.ConversationId, telemetry.MessageId));

        // The exec-log row is written first, then extraction runs and throws; the row landing proves the job executed and
        // the throw was contained (the test process did not fault on an unobserved exception).
        _ = await PollForLogAsync(provider, agentId).ConfigureAwait(false);
        await extraction.Received(1).ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>())
                        .ConfigureAwait(false);
    }

    [Test]
    public async Task Dispatch_RunsExtractionOnWorkerDrainTokenNotTheSendToken()
    {
        var agentId = Guid.NewGuid();
        var capturedToken = new CancellationToken(true); // start canceled so a no-op would fail the assert
        var captured = false;
        var extraction = Substitute.For<IMemoryExtractionService>();
        extraction.When(service => service.ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>()))
                  .Do(call =>
                  {
                      capturedToken = call.Arg<CancellationToken>();
                      captured = true;
                  });
        await using var provider = await BuildProviderAsync("exec-log-fresh-token.sqlite", extraction).ConfigureAwait(false);
        await using var pipeline = await Pipeline.StartAsync(provider).ConfigureAwait(false);

        var telemetry = Telemetry(agentId);

        // The worker runs the job on its OWN drain token — never the originating send token — so a cancellation of the
        // send can never abort extraction of an already-completed run. During normal operation that token is uncancelled.
        pipeline.Dispatcher.Dispatch(telemetry, Run(agentId, telemetry.ConversationId, telemetry.MessageId));

        await AssertEx.EventuallyAsync(() => captured,
            TimeSpan.FromSeconds(5),
            "The background worker should have invoked extraction.").ConfigureAwait(false);
        AssertEx.False(capturedToken.IsCancellationRequested,
            "Extraction must run on the worker's uncancelled drain token, decoupled from any send cancellation.");
    }

    [Test]
    public async Task Dispatch_WhenQueueIsFull_DropsExcessJobsWithoutBlocking()
    {
        var agentId = Guid.NewGuid();
        await using var provider = await BuildProviderAsync("exec-log-full-queue.sqlite").ConfigureAwait(false);

        // Enqueue past the bounded capacity BEFORE the worker starts draining: exactly QueueCapacity jobs are accepted
        // and the rest are dropped (never blocking the caller). Starting the worker afterwards drains only the accepted
        // jobs, so exactly QueueCapacity exec-log rows land — proving the queue is bounded, not unbounded fire-and-forget.
        var options = new MemoryExtractionOptions { QueueCapacity = 2 };
        var optionsAccessor = Options.Create(options);
        var dispatcher = new MemoryExtractionDispatcher(optionsAccessor, NullLogger<MemoryExtractionDispatcher>.Instance);

        for (var i = 0; i < 5; i++)
        {
            dispatcher.Dispatch(Telemetry(agentId), Run(agentId, Guid.NewGuid(), Guid.NewGuid()));
        }

        using var worker = new MemoryExtractionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            optionsAccessor,
            NullLogger<MemoryExtractionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await AssertEx.EventuallyAsync(() => CountRows(provider, agentId) >= 2,
                TimeSpan.FromSeconds(5),
                "The worker should have drained the two accepted jobs.").ConfigureAwait(false);

            // No third job was ever accepted, so the count cannot climb past the capacity.
            AssertEx.Equal(expected: 2, CountRows(provider, agentId));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static MemoryExtractionDispatchContext Telemetry(Guid agentId)
    {
        return new MemoryExtractionDispatchContext(agentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "qwen3:8b",
            "config-hash",
            LatencyMs: 100,
            Success: true,
            PromptTokens: null,
            CompletionTokens: null,
            ErrorClass: null);
    }

    private static MemoryExtractionRunInput Run(Guid agentId, Guid conversationId, Guid messageId, bool failed = false)
    {
        return new MemoryExtractionRunInput(agentId,
            conversationId,
            messageId,
            [new MemoryExtractionTurn("hello")],
            "answer",
            failed,
            failed ? "boom" : null,
            MemoryExcluded: false);
    }

    private static int CountRows(ServiceProvider provider, Guid agentId)
    {
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();
        return store.ListByAgentAsync(agentId, limit: 100).GetAwaiter().GetResult().Count;
    }

    private static async Task<AgentExecutionLogRecord> PollForLogAsync(ServiceProvider provider, Guid agentId)
    {
        AgentExecutionLogRecord? found = null;
        await AssertEx.EventuallyAsync(() =>
            {
                using var scope = provider.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();
                var rows = store.ListByAgentAsync(agentId, limit: 10).GetAwaiter().GetResult();
                if (rows.Count > 0)
                {
                    found = rows[0];
                    return true;
                }

                return false;
            },
            TimeSpan.FromSeconds(5),
            "The background worker should have written an execution-log row.").ConfigureAwait(false);

        return AssertEx.NotNull(found, "An execution-log row should have been written.");
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName, IMemoryExtractionService? extractionService = null)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAgentExecutionLogStore, AgentExecutionLogStore>();
        // The extraction service is not under test here (its behavior is covered by MemoryExtractionServiceTests); a
        // substitute keeps the pipeline focused on the exec-log write + scope/isolation + bounded-queue contract. Tests
        // that exercise the background failure/cancellation contract pass a pre-configured substitute.
        var extraction = extractionService ?? Substitute.For<IMemoryExtractionService>();
        services.AddScoped(_ => extraction);

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    // Starts the dispatcher + hosted worker together and tears them down on dispose, mirroring production wiring.
    private sealed class Pipeline : IAsyncDisposable
    {
        private readonly MemoryExtractionWorker _worker;

        private Pipeline(MemoryExtractionDispatcher dispatcher, MemoryExtractionWorker worker)
        {
            Dispatcher = dispatcher;
            _worker = worker;
        }

        public MemoryExtractionDispatcher Dispatcher { get; }

        public static async Task<Pipeline> StartAsync(ServiceProvider provider, MemoryExtractionOptions? options = null)
        {
            var optionsAccessor = Options.Create(options ?? new MemoryExtractionOptions());
            var dispatcher = new MemoryExtractionDispatcher(optionsAccessor, NullLogger<MemoryExtractionDispatcher>.Instance);
            var worker = new MemoryExtractionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
                dispatcher,
                optionsAccessor,
                NullLogger<MemoryExtractionWorker>.Instance);
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            return new Pipeline(dispatcher, worker);
        }

        public async ValueTask DisposeAsync()
        {
            await _worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _worker.Dispose();
        }
    }
}
