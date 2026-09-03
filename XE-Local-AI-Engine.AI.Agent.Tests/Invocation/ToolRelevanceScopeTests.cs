namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The turn-scoped carrier for the tool-relevance decision. Two mechanisms are pinned here, and neither substitutes
///     for the other: the ambient slot holds a MUTABLE holder, so a write several awaited frames below the opener is
///     visible to it; and the per-array store is exactly-once, so concurrent rounds on one tool array pay a single
///     selection rather than one apiece. The cancellation rules are the subtle half — a caller's own token aborts only
///     that caller's wait, never the shared computation every other waiter is awaiting.
/// </summary>
public sealed class ToolRelevanceScopeTests
{
    [Test]
    public void Current_WithoutAScope_IsNull()
    {
        AssertEx.Null(ToolRelevanceScope.Current, "No scope means no filtering, which is the shipped default.");
    }

    [Test]
    public void Scope_OnDispose_RestoresThePriorValueRatherThanClearingIt()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames("outer")))
        {
            var outer = AssertEx.NotNull(ToolRelevanceScope.Current);

            using (ToolRelevanceScope.BeginScope(active: false, CoreNames("inner")))
            {
                AssertEx.False(AssertEx.NotNull(ToolRelevanceScope.Current).Active);
            }

            AssertEx.True(ReferenceEquals(outer, ToolRelevanceScope.Current), "A nested seed must not leak into the outer turn.");
        }

        AssertEx.Null(ToolRelevanceScope.Current);
    }

    [Test]
    public async Task Reveal_WrittenInAnAwaitedCallee_IsVisibleToTheOpener()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            await RevealInACalleeAsync();

            var decision = await AssertEx.NotNull(ToolRelevanceScope.Current).GetOrComputeAsync(Key("a", "b"), ThrowingFactory, CancellationToken.None);
            AssertEx.True(decision.IsRevealed("hidden_tool"), "The slot holds a mutable holder, so a callee's write reaches the opener.");
        }

        static async Task RevealInACalleeAsync()
        {
            await Task.Yield();
            var decision = await AssertEx.NotNull(ToolRelevanceScope.Current).GetOrComputeAsync(Key("a", "b"), () => Task.FromResult(Decision(["a"], ["hidden_tool"])), CancellationToken.None);
            decision.Reveal(["hidden_tool"]);
        }
    }

    [Test]
    public async Task GetOrComputeAsync_WithTwoDifferentKeys_KeepsIndependentDecisions()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);

            var first = await state.GetOrComputeAsync(Key("a", "b"), () => Task.FromResult(Decision(["a"], ["b"])), CancellationToken.None);
            var second = await state.GetOrComputeAsync(Key("c", "d"), () => Task.FromResult(Decision(["c"], ["d"])), CancellationToken.None);

            first.Reveal(["b"]);

            AssertEx.False(ReferenceEquals(first, second), "Every distinct tool array gets its own decision.");
            AssertEx.False(second.IsRevealed("b"), "A reveal on one array must never reach another's decision.");
        }
    }

    [Test]
    public async Task Reveal_FromConcurrentCallers_LosesNothing()
    {
        var decision = Decision(["a"], [.. Enumerable.Range(0, 50).Select(static index => $"hidden_{index}")]);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(index => Task.Run(() => decision.Reveal([$"hidden_{index}"]))));

        for (var index = 0; index < 50; index++)
        {
            AssertEx.True(decision.IsRevealed($"hidden_{index}"), "The reveal union is lock-free but must lose nothing.");
        }
    }

    [Test]
    public async Task GetOrComputeAsync_WithTenConcurrentCallersOnOneKey_RunsTheFactoryExactlyOnce()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);
            var gate = new TaskCompletionSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var invocations = 0;

            // Released TOGETHER off the thread pool. Created sequentially on one thread, callers 2..10 could only ever
            // find an already-published entry, and the test would still pass against a plain GetOrAdd that runs its
            // factory more than once — the exact defect the single-flight store exists to rule out.
            var callers = Enumerable.Range(0, 10)
                                    .Select(caller => Task.Run(async () =>
                                    {
                                        await start.Task;
                                        return await state.GetOrComputeAsync(Key("a", "b"),
                                            async () =>
                                            {
                                                _ = Interlocked.Increment(ref invocations);
                                                _ = entered.TrySetResult();
                                                await gate.Task;
                                                return Decision(["a"], ["b"]);
                                            },
                                            CancellationToken.None);
                                    }))
                                    .ToList();

            start.SetResult();
            await entered.Task;
            gate.SetResult();
            _ = await Task.WhenAll(callers);

            AssertEx.Equal(expected: 1, Volatile.Read(ref invocations), "One selection per tool array per turn, not one per round.");
        }
    }

    [Test]
    public async Task GetOrComputeAsync_WithTenConcurrentCallersOnOneKey_ReturnsTheSameDecisionInstanceToAll()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);
            var gate = new TaskCompletionSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var callers = Enumerable.Range(0, 10)
                                    .Select(caller => Task.Run(async () =>
                                    {
                                        await start.Task;
                                        return await state.GetOrComputeAsync(Key("a", "b"),
                                            async () =>
                                            {
                                                _ = entered.TrySetResult();
                                                await gate.Task;
                                                return Decision(["a"], ["b"]);
                                            },
                                            CancellationToken.None);
                                    }))
                                    .ToList();

            start.SetResult();
            await entered.Task;
            gate.SetResult();
            var decisions = await Task.WhenAll(callers);

            AssertEx.True(decisions.All(decision => ReferenceEquals(decision, decisions[0])), "A shared decision is what makes a reveal visible to every round.");
        }
    }

    [Test]
    public async Task GetOrComputeAsync_WhenOneCallerCancels_TheOtherWaitersStillGetTheDecision()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);
            var gate = new TaskCompletionSource();
            var invocations = 0;
            using var cancelledCaller = new CancellationTokenSource();

            Task<ArrayDecision> Caller(CancellationToken token)
            {
                return state.GetOrComputeAsync(Key("a", "b"),
                    async () =>
                    {
                        _ = Interlocked.Increment(ref invocations);
                        await gate.Task;
                        return Decision(["a"], ["b"]);
                    },
                    token);
            }

            var first = Caller(cancelledCaller.Token);
            var second = Caller(CancellationToken.None);

            await cancelledCaller.CancelAsync();
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await first);

            // The shared computation was never the cancelled caller's to cancel: completing it now still serves the
            // co-waiter. This is the test that fails if a caller token is flowed into the shared factory.
            gate.SetResult();
            var decision = await second;

            AssertEx.Equal(expected: 1, Volatile.Read(ref invocations));
            AssertEx.Contains(decision.HiddenNames, "b");
        }
    }

    [Test]
    public async Task GetOrComputeAsync_WhenOneCallerCancels_KeepsTheEntrySoTheNextCallerReusesIt()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);
            var gate = new TaskCompletionSource();
            var invocations = 0;
            using var cancelledCaller = new CancellationTokenSource();

            Task<ArrayDecision> Caller(CancellationToken token)
            {
                return state.GetOrComputeAsync(Key("a", "b"),
                    async () =>
                    {
                        _ = Interlocked.Increment(ref invocations);
                        await gate.Task;
                        return Decision(["a"], ["b"]);
                    },
                    token);
            }

            var abandoned = Caller(cancelledCaller.Token);
            await cancelledCaller.CancelAsync();
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await abandoned);

            gate.SetResult();
            _ = await Caller(CancellationToken.None);

            // The pre-first-token retry re-invokes the whole send factory; it must reuse the decision rather than pay a
            // second embedding round-trip.
            AssertEx.Equal(expected: 1, Volatile.Read(ref invocations), "A caller's cancelled wait evicts nothing.");
        }
    }

    [Test]
    public async Task GetOrComputeAsync_WhenTheSharedComputationFaults_EvictsTheEntryAndRecomputesOnTheNextCall()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);

            _ = await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
                await state.GetOrComputeAsync(Key("a", "b"), static () => Task.FromException<ArrayDecision>(new InvalidOperationException("selector broke")), CancellationToken.None));

            var recovered = await state.GetOrComputeAsync(Key("a", "b"), () => Task.FromResult(Decision(["a"], ["b"])), CancellationToken.None);

            AssertEx.Contains(recovered.OfferedNames, "a", "A faulted shared computation must not poison the entry for the rest of the turn.");
        }
    }

    [Test]
    public async Task GetOrComputeAsync_WhenTheSharedComputationIsCancelled_EvictsTheEntryAndRecomputesOnTheNextCall()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);

            // The factory cancels itself — an OperationCanceledException escaping the selector, which is what an
            // HttpClient timeout inside the embedding path looks like. The callers' own tokens are untouched, so this
            // is a terminal state of the SHARED task and it must not be cached: a cached cancellation would rethrow
            // out of every later round, including the pre-first-token retry.
            using var factoryToken = new CancellationTokenSource();
            await factoryToken.CancelAsync();

            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () =>
                await state.GetOrComputeAsync(Key("a", "b"),
                    () => Task.FromCanceled<ArrayDecision>(factoryToken.Token),
                    CancellationToken.None));

            var recovered = await state.GetOrComputeAsync(Key("a", "b"), () => Task.FromResult(Decision(["a"], ["b"])), CancellationToken.None);

            AssertEx.Contains(recovered.OfferedNames, "a", "A cancelled shared computation must not poison the entry for the rest of the turn.");
        }
    }

    [Test]
    public async Task GetOrComputeAsync_WhenTheOnlyWaiterCancelsAndTheSharedTaskThenFaults_TheNextCallerRecomputes()
    {
        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);
            var gate = new TaskCompletionSource();
            var invocations = 0;
            using var cancelledCaller = new CancellationTokenSource();

            // The only waiter abandons its wait, and only THEN does the shared computation fail. Nobody is inside the
            // finally at that moment, so waiter-side eviction alone would leave the dead task cached and hand it
            // straight to the next round, which would degrade to the unfiltered offer without even trying.
            var abandoned = state.GetOrComputeAsync(Key("a", "b"),
                async () =>
                {
                    _ = Interlocked.Increment(ref invocations);
                    await gate.Task;
                    throw new InvalidOperationException("the embedding provider timed out");
                },
                cancelledCaller.Token);

            await cancelledCaller.CancelAsync();
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await abandoned);

            gate.SetResult();
            await AssertEx.EventuallyAsync(() => !state.HasDecision(Key("a", "b")),
                TimeSpan.FromSeconds(5),
                "The shared task's own continuation evicts the entry with no waiter present.");

            var recovered = await state.GetOrComputeAsync(Key("a", "b"),
                () =>
                {
                    _ = Interlocked.Increment(ref invocations);
                    return Task.FromResult(Decision(["a"], ["b"]));
                },
                CancellationToken.None);

            AssertEx.Contains(recovered.OfferedNames, "a", "The next caller must get a fresh computation, not the stale fault.");
            AssertEx.Equal(expected: 2, Volatile.Read(ref invocations), "The abandoned attempt and the recovery are two runs — the second was not served the cached fault.");
        }
    }

    [Test]
    public async Task ArrayKey_WhenTwoNameSequencesHashAlike_AreNotEqualAndKeepTwoDecisions()
    {
        var left = new ArrayKey(hash: 42, ["a", "b"]);
        var right = new ArrayKey(hash: 42, ["c", "d"]);

        AssertEx.False(left.Equals(right), "Equality compares the names too, so a hash collision is never a silent share.");

        using (ToolRelevanceScope.BeginScope(active: true, CoreNames()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);

            var first = await state.GetOrComputeAsync(left, () => Task.FromResult(Decision(["a"], ["b"])), CancellationToken.None);
            var second = await state.GetOrComputeAsync(right, () => Task.FromResult(Decision(["c"], ["d"])), CancellationToken.None);

            AssertEx.False(ReferenceEquals(first, second), "Two colliding arrays keep two distinct decisions.");
            AssertEx.Contains(second.OfferedNames, "c");
        }
    }

    private static Task<ArrayDecision> ThrowingFactory()
    {
        throw new AssertionException("The decision was already computed for this key; the factory must not run again.");
    }

    private static ArrayKey Key(params string[] names)
    {
        return new ArrayKey(names);
    }

    private static ArrayDecision Decision(string[] offered, string[] hidden)
    {
        return new ArrayDecision
        {
            OfferedNames = offered,
            HiddenNames = hidden
        };
    }

    private static IReadOnlySet<string> CoreNames(params string[] names)
    {
        return new HashSet<string>(names, StringComparer.Ordinal);
    }
}
