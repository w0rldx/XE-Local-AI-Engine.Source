namespace XE_Local_AI_Engine.Tests.Memory;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Tests for the background <see cref="MemoryExtractionDispatcher" />: it writes a metadata-only
///     <c>AgentExecutionLog</c> row (no message content; tokens degrade to null) on its OWN scope/DbContext, runs the
///     extraction service, and never throws into the caller. The dispatch is fire-and-forget, so assertions poll for the
///     background work to land.
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
        var dispatcher = new MemoryExtractionDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MemoryExtractionDispatcher>.Instance);

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

        dispatcher.Dispatch(telemetry, Run(agentId, conversationId, messageId));

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
        var dispatcher = new MemoryExtractionDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MemoryExtractionDispatcher>.Instance);

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

        dispatcher.Dispatch(telemetry, Run(agentId, telemetry.ConversationId, telemetry.MessageId, failed: true));

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
        // A failure inside the background extraction must be swallowed by the dispatcher's catch-all — it must never
        // surface to the (fire-and-forget) caller nor escape as an unobserved task exception that affects the chat path.
        extraction.When(service => service.ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>()))
                  .Do(_ => throw new InvalidOperationException("extraction blew up"));
        await using var provider = await BuildProviderAsync("exec-log-throw.sqlite", extraction).ConfigureAwait(false);
        var dispatcher = new MemoryExtractionDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MemoryExtractionDispatcher>.Instance);

        var telemetry = Telemetry(agentId);

        // Dispatch is void/fire-and-forget: it must return normally even though the background extraction will throw.
        dispatcher.Dispatch(telemetry, Run(agentId, telemetry.ConversationId, telemetry.MessageId));

        // The exec-log row is written first, then extraction runs and throws; the row landing proves the job executed and
        // the throw was contained (the test process did not fault on an unobserved exception).
        _ = await PollForLogAsync(provider, agentId).ConfigureAwait(false);
        await extraction.Received(1).ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>())
                        .ConfigureAwait(false);
    }

    [Test]
    public async Task Dispatch_RunsExtractionOnFreshNonCancelableToken()
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
        var dispatcher = new MemoryExtractionDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MemoryExtractionDispatcher>.Instance);

        var telemetry = Telemetry(agentId);

        // Dispatch takes NO caller token; it must run the job on a FRESH, non-cancelable token so a cancellation of the
        // originating send can never abort extraction of an already-completed run.
        dispatcher.Dispatch(telemetry, Run(agentId, telemetry.ConversationId, telemetry.MessageId));

        await AssertEx.EventuallyAsync(() => captured,
            TimeSpan.FromSeconds(5),
            "The background dispatcher should have invoked extraction.").ConfigureAwait(false);
        AssertEx.False(capturedToken.CanBeCanceled,
            "Extraction must run on a non-cancelable token (CancellationToken.None) so a send cancellation cannot abort it.");
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
            "The background dispatcher should have written an execution-log row.").ConfigureAwait(false);

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
        // substitute keeps the dispatcher focused on the exec-log write + scope/isolation contract. Tests that exercise
        // the background failure/cancellation contract pass a pre-configured substitute.
        var extraction = extractionService ?? Substitute.For<IMemoryExtractionService>();
        services.AddScoped(_ => extraction);

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }
}
