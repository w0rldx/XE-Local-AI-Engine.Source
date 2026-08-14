namespace XE_Local_AI_Engine.Client.Persistence.Tests.Models;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;

public sealed class ModelCoordinationTests
{
    [Test]
    public async Task MapBatch_NormalizesDeduplicatesAndOrdersKeys()
    {
        var coordinator = new ModelProviderMapLeaseCoordinator(new KeyedCompositeLockDomain());
        await using var lease = await coordinator.AcquireMapReadAsync(new[] { " zeta ", "ALPHA", "alpha" });

        AssertEx.Equal(expected: 2, lease.MapKeys.Count);
        AssertEx.Equal("2:provider-map:ALPHA", lease.MapKeys[0]);
        AssertEx.Equal("2:provider-map:ZETA", lease.MapKeys[1]);
    }

    [Test]
    public async Task MutationWait_CancellationLeaksNoReservation()
    {
        var domain = new KeyedCompositeLockDomain();
        await using var owner = await domain.AcquireMutationAsync(new[] { "0:model:ALPHA", "1:path:a.gguf" });
        using var cancellation = new CancellationTokenSource();
        var waiting = RunWithoutExecutionContext(() => domain.AcquireMutationAsync(new[] { "1:path:a.gguf", "2:provider-map:ALPHA" }, cancellation.Token).AsTask());
        cancellation.Cancel();
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => waiting);

        await owner.DisposeAsync();
        var acquired = await RunWithoutExecutionContext(() => domain.AcquireMutationAsync(new[] { "1:path:a.gguf" }).AsTask());
        await acquired.DisposeAsync();
    }

    [Test]
    public async Task CompositeReadsCoexistAndBlockOverlappingMutation()
    {
        var domain = new KeyedCompositeLockDomain();
        await using var first = await domain.AcquireReadAsync(new[] { "0:model:ALPHA", "1:path:a.gguf" });
        var secondTask = RunWithoutExecutionContext(() => domain.AcquireReadAsync(new[] { "1:path:a.gguf" }).AsTask());
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        var writerTask = RunWithoutExecutionContext(() => domain.AcquireMutationAsync(new[] { "1:path:a.gguf" }).AsTask());

        await Task.Delay(50);
        AssertEx.False(writerTask.IsCompleted, "An overlapping mutation must wait for every read lease.");
        await second.DisposeAsync();
        await first.DisposeAsync();
        await using var writer = await writerTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task SameFlowNestedAcquisitionIsRejected()
    {
        var domain = new KeyedCompositeLockDomain();
        await using var lease = await domain.AcquireReadAsync(new[] { "2:provider-map:ALPHA" });

        _ = AssertEx.Throws<InvalidOperationException>(() => domain.AcquireReadAsync(new[] { "2:provider-map:BETA" }));
    }

    [Test]
    public void DeterministicIdentity_MatchesGoldenAndCaseVariantsShareReservation()
    {
        var resolver = CreateIdentityResolver();
        var lower = resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Import, "foo", "Q4_K_M"));
        var upper = resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Download, "FOO", "q4_k_m"));

        AssertEx.Equal("foo:Q4_K_M", lower.CanonicalModelName);
        AssertEx.Equal("foo-Q4_K_M-849525de9efce6742c0cf2b6.gguf", lower.FinalFileName);
        AssertEx.Equal(lower.ModelReservationKey, upper.ModelReservationKey);
        AssertEx.True(lower.FinalFileName.EndsWith("849525de9efce6742c0cf2b6.gguf", StringComparison.Ordinal));
        AssertEx.True(upper.FinalFileName.EndsWith("849525de9efce6742c0cf2b6.gguf", StringComparison.Ordinal));
    }

    [Test]
    public void DeterministicIdentity_SlugIsContainedAndBounded()
    {
        var resolver = CreateIdentityResolver();
        var identity = resolver.Resolve(new GgufAcquisitionIntent(
            GgufAcquisitionOperationKind.Import,
            new string('A', 90),
            "F16"));

        AssertEx.False(identity.FinalFileName.Contains("..", StringComparison.Ordinal));
        AssertEx.Equal(expected: 72, identity.FinalFileName[..identity.FinalFileName.IndexOf("-F16-", StringComparison.Ordinal)].Length);
    }

    private static GgufAcquisitionIdentityResolver CreateIdentityResolver()
    {
        return new GgufAcquisitionIdentityResolver(new ModelNameValidator(Options.Create(new SecurityOptions())));
    }

    private static Task<T> RunWithoutExecutionContext<T>(Func<Task<T>> action)
    {
        Task<T> task;
        using (ExecutionContext.SuppressFlow())
        {
            task = Task.Run(action);
        }

        return task;
    }
}
