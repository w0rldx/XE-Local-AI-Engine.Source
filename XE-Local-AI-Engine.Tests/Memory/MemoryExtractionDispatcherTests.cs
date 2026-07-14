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
using XE_Local_AI_Engine.Tests.CodexOAuth;
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
        // Signalled from inside the substitute the moment ExtractAsync is entered, so the assertions synchronize on the
        // actual call rather than on the exec-log row (which the worker writes BEFORE calling ExtractAsync — polling the
        // row and then asserting Received(1) races that ordering).
        var extractionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A failure inside the background extraction must be swallowed by the worker's catch-all — it must never surface
        // to the (fire-and-forget) caller nor escape as an unobserved task exception that affects the chat path.
        extraction.When(service => service.ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>()))
                  .Do(_ =>
                  {
                      extractionEntered.TrySetResult();
                      throw new InvalidOperationException("extraction blew up");
                  });
        await using var provider = await BuildProviderAsync("exec-log-throw.sqlite", extraction).ConfigureAwait(false);
        await using var pipeline = await Pipeline.StartAsync(provider).ConfigureAwait(false);

        var telemetry = Telemetry(agentId);

        // Dispatch is void/fire-and-forget: it must return normally even though the background extraction will throw.
        pipeline.Dispatcher.Dispatch(telemetry, Run(agentId, telemetry.ConversationId, telemetry.MessageId));

        // Wait for ExtractAsync to actually be entered, THEN assert. The row is also written (before the call), and the
        // throw was contained — the test process did not fault on an unobserved exception.
        await extractionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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

    [Test]
    public async Task StopAsync_ExecutesJobsStillQueuedAtShutdown_WithinTheDeadline()
    {
        var agentId = Guid.NewGuid();

        // A single-slot worker whose FIRST extraction parks until released: while job #1 is parked in ExtractAsync the
        // other dispatched jobs stay queued/unstarted. The drain window is generous (never trips), so the worker must
        // DRAIN those queued jobs (run them) at shutdown rather than silently drop them.
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var extraction = Substitute.For<IMemoryExtractionService>();
        extraction.ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>())
                  .Returns(async _ =>
                  {
                      if (Interlocked.Increment(ref calls) == 1)
                      {
                          firstEntered.TrySetResult();
                          await release.Task.ConfigureAwait(false);
                      }

                      return MemoryExtractionOutcome.NoModelConfigured();
                  });

        await using var provider = await BuildProviderAsync("exec-log-drain-queued.sqlite", extraction).ConfigureAwait(false);
        var optionsAccessor = Options.Create(new MemoryExtractionOptions { MaxConcurrentExtractions = 1, ShutdownDrainTimeoutSeconds = 30 });
        var dispatcher = new MemoryExtractionDispatcher(optionsAccessor, NullLogger<MemoryExtractionDispatcher>.Instance);
        using var worker = new MemoryExtractionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            optionsAccessor,
            NullLogger<MemoryExtractionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

        // #1 parks in ExtractAsync (holding the one slot); #2 and #3 remain queued/unstarted.
        for (var i = 0; i < 3; i++)
        {
            dispatcher.Dispatch(Telemetry(agentId), Run(agentId, Guid.NewGuid(), Guid.NewGuid()));
        }

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Stop while #2 and #3 are still queued. StopAsync must not return until they are drained; releasing #1 lets the
        // drain proceed and finish inside the (generous) window.
        var stop = worker.StopAsync(CancellationToken.None);
        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        // All three ran — none was silently dropped when the worker stopped (the pre-fix worker drained only #1).
        AssertEx.Equal(expected: 3, CountRows(provider, agentId));
    }

    [Test]
    public async Task Dispatch_AfterWorkerStopped_TakesTheDroppedPathAndEnqueuesNothing()
    {
        var agentId = Guid.NewGuid();
        await using var provider = await BuildProviderAsync("exec-log-dispatch-after-stop.sqlite").ConfigureAwait(false);
        var optionsAccessor = Options.Create(new MemoryExtractionOptions());
        var dispatcher = new MemoryExtractionDispatcher(optionsAccessor, NullLogger<MemoryExtractionDispatcher>.Instance);
        using var worker = new MemoryExtractionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            optionsAccessor,
            NullLogger<MemoryExtractionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);

        // The worker completed the writer at shutdown, so a late Dispatch takes the dropped path: it must not throw, must
        // not enqueue anything, and no execution-log row can ever land for it.
        dispatcher.Dispatch(Telemetry(agentId), Run(agentId, Guid.NewGuid(), Guid.NewGuid()));

        AssertEx.Equal(expected: 0, dispatcher.Reader.Count);
        AssertEx.Equal(expected: 0, CountRows(provider, agentId));
    }

    [Test]
    public async Task StopAsync_WhenDrainDeadlineElapses_CompletesCleanlyWithoutObjectDisposed()
    {
        var agentId = Guid.NewGuid();

        // A job that only ends when the drain token is cancelled: it parks in ExtractAsync on the passed token. With the
        // minimum 1s drain window the window elapses, the worker cancels the drain token, the job unwinds via that
        // cancellation, and StopAsync awaits its completion BEFORE returning — so Dispose never races a straggler into a
        // disposed CTS/semaphore.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extraction = Substitute.For<IMemoryExtractionService>();
        extraction.ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>())
                  .Returns(async callInfo =>
                  {
                      entered.TrySetResult();
                      await Task.Delay(Timeout.Infinite, callInfo.Arg<CancellationToken>()).ConfigureAwait(false);
                      return MemoryExtractionOutcome.NoModelConfigured();
                  });

        await using var provider = await BuildProviderAsync("exec-log-deadline.sqlite", extraction).ConfigureAwait(false);
        var logger = new CapturingLogger<MemoryExtractionWorker>();
        var optionsAccessor = Options.Create(new MemoryExtractionOptions { MaxConcurrentExtractions = 1, ShutdownDrainTimeoutSeconds = 1 });
        var dispatcher = new MemoryExtractionDispatcher(optionsAccessor, NullLogger<MemoryExtractionDispatcher>.Instance);
        var worker = new MemoryExtractionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            optionsAccessor,
            logger);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

        dispatcher.Dispatch(Telemetry(agentId), Run(agentId, Guid.NewGuid(), Guid.NewGuid()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // StopAsync returns on its own once the 1s window elapses (well under this generous cap); Dispose must not throw.
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        worker.Dispose();

        var log = logger.AllText;
        AssertEx.True(log.Contains("shutdown drain exceeded", StringComparison.Ordinal),
            "The deadline-expiry path should log the content-free drain-exceeded warning.");
        AssertEx.False(log.Contains("ObjectDisposed", StringComparison.Ordinal),
            "The straggler must unwind via cancellation before Dispose — no ObjectDisposedException should surface.");
    }

    [Test]
    public async Task StopAsync_WhenJobIgnoresCancellation_AbandonsWithinBoundsLogsCountAndSurvivesRelease()
    {
        var agentId = Guid.NewGuid();

        // A job that IGNORES cooperative cancellation entirely: it blocks on a TCS that is NOT tied to the drain token, so
        // cancelling that token cannot unblock it (unlike the cooperative straggler in the sibling test, which parks on
        // Task.Delay(Timeout.Infinite, token)). With the minimum 1s window plus the fixed 2s grace, StopAsync must STILL
        // return promptly, count the one abandoned job, log it content-free, and Dispose without throwing — the bounded,
        // honest shutdown contract. Releasing the job AFTER Dispose must not crash the process (the disposed-semaphore /
        // catch-all swallow paths hold).
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extraction = Substitute.For<IMemoryExtractionService>();
        extraction.ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>())
                  .Returns(async _ =>
                  {
                      entered.TrySetResult();
                      try
                      {
                          // Deliberately does NOT observe the passed cancellation token: only the explicit release ends it.
                          await release.Task.ConfigureAwait(false);
                      }
                      finally
                      {
                          resumed.TrySetResult();
                      }

                      return MemoryExtractionOutcome.NoModelConfigured();
                  });

        await using var provider = await BuildProviderAsync("exec-log-abandon.sqlite", extraction).ConfigureAwait(false);
        var logger = new CapturingLogger<MemoryExtractionWorker>();
        var optionsAccessor = Options.Create(new MemoryExtractionOptions { MaxConcurrentExtractions = 1, ShutdownDrainTimeoutSeconds = 1 });
        var dispatcher = new MemoryExtractionDispatcher(optionsAccessor, NullLogger<MemoryExtractionDispatcher>.Instance);
        var worker = new MemoryExtractionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            optionsAccessor,
            logger);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

        dispatcher.Dispatch(Telemetry(agentId), Run(agentId, Guid.NewGuid(), Guid.NewGuid()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Even though the job never observes cancellation, StopAsync completes within bounds (1s window + 2s grace + slack)
        // and Dispose does not throw despite the still-running straggler.
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        worker.Dispose();

        var log = logger.AllText;
        AssertEx.True(log.Contains("Abandoned 1 memory-extraction job(s) that ignored cancellation", StringComparison.Ordinal),
            "The abandoned path must log the explicit content-free abandoned-count warning.");
        AssertEx.False(log.Contains("ObjectDisposed", StringComparison.Ordinal),
            "Abandonment is expected and swallowed — no ObjectDisposedException should surface in the log.");

        // Release the abandoned job AFTER Dispose: on resume its finally hits ReleaseConcurrency against the disposed
        // semaphore (ObjectDisposedException, swallowed) and the catch-all contains any fault. Nothing may crash the
        // process — the job simply runs to completion and its self-removing continuation drops it from the set.
        release.TrySetResult();
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEx.False(logger.AllText.Contains("ObjectDisposed", StringComparison.Ordinal),
            "Releasing the abandoned job after Dispose must be swallowed silently — no ObjectDisposedException may surface.");
    }

    [Test]
    public async Task StopAsync_WhenSecondJobAwaitsConcurrencySlotAtShutdown_AccountsItAndDropsNothingSilently()
    {
        var agentId = Guid.NewGuid();

        // Regression guard for the dequeue-then-await-slot accounting gap. MaxConcurrentExtractions=1: job #1 takes the one
        // slot and job #2 is then buffered while the read loop blocks awaiting that (held) slot to start it. When the drain
        // window elapses, the read loop's slot-wait is cancelled while #2 is still pending — the exact moment that, under the
        // old ReadAllAsync ordering, had already pulled #2 off the channel (so the queued-drain missed it) yet never reached
        // TrackInFlight (so _inFlight missed it), letting it escape BOTH the dropped and abandoned counters. #1 deliberately
        // IGNORES cancellation so the read loop's ONLY exit is cancellation (nothing frees the slot to race it into starting
        // #2) — this pins #2 on the channel deterministically. Shutdown must then account for BOTH: #1 as an abandoned
        // in-flight job, #2 as a dropped queued job. Total dropped + abandoned covers both; nothing silently vanishes.
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var extraction = Substitute.For<IMemoryExtractionService>();
        extraction.ExtractAsync(Arg.Any<MemoryExtractionRunInput>(), Arg.Any<CancellationToken>())
                  .Returns(async _ =>
                  {
                      if (Interlocked.Increment(ref calls) == 1)
                      {
                          firstEntered.TrySetResult();
                          try
                          {
                              // Does NOT observe the passed token: only the explicit release ends it, so #1 stays in-flight
                              // (never freeing the slot) through the drain window and grace — it is the abandoned straggler.
                              await release.Task.ConfigureAwait(false);
                          }
                          finally
                          {
                              resumed.TrySetResult();
                          }
                      }

                      return MemoryExtractionOutcome.NoModelConfigured();
                  });

        await using var provider = await BuildProviderAsync("exec-log-pending-slot.sqlite", extraction).ConfigureAwait(false);
        var logger = new CapturingLogger<MemoryExtractionWorker>();
        var optionsAccessor = Options.Create(new MemoryExtractionOptions { MaxConcurrentExtractions = 1, ShutdownDrainTimeoutSeconds = 1 });
        var dispatcher = new MemoryExtractionDispatcher(optionsAccessor, NullLogger<MemoryExtractionDispatcher>.Instance);
        var worker = new MemoryExtractionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            optionsAccessor,
            logger);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

        // #1 takes the one slot and parks; only after it is confirmed running do we buffer #2, so #2 is guaranteed to be the
        // job left pending on the channel with the read loop blocked awaiting the slot when StopAsync fires.
        dispatcher.Dispatch(Telemetry(agentId), Run(agentId, Guid.NewGuid(), Guid.NewGuid()));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        dispatcher.Dispatch(Telemetry(agentId), Run(agentId, Guid.NewGuid(), Guid.NewGuid()));

        // The 1s window elapses with #2 still pending; StopAsync cancels the drain token, abandons the cancellation-ignoring
        // #1, and drains #2 as a dropped queued job — returning within bounds (1s window + 2s grace + slack).
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        worker.Dispose();

        // Both jobs are accounted, not silently lost: the content-free drain-exceeded warning reports #1 abandoned and #2
        // dropped in one line. The pre-fix worker reported "abandoned 1 ... dropped 0" here — #2 had vanished from both.
        var log = logger.AllText;
        AssertEx.True(log.Contains("abandoned 1 in-flight and dropped 1 queued extraction(s)", StringComparison.Ordinal),
            "The pending job awaiting the concurrency slot at shutdown must be accounted as a dropped queued extraction, never silently lost from both counters.");
        AssertEx.False(log.Contains("ObjectDisposed", StringComparison.Ordinal),
            "Abandonment is expected and swallowed — no ObjectDisposedException should surface in the log.");

        // Release the abandoned job AFTER Dispose so it runs to completion cleanly (its finally hits the disposed-semaphore
        // swallow path) and the test process is not left with a parked background task.
        release.TrySetResult();
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
