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
        await using var lease = await coordinator.AcquireMapReadAsync(new[]
        {
            " zeta ",
            "ALPHA",
            "alpha"
        });

        AssertEx.Equal(expected: 2, lease.MapKeys.Count);
        AssertEx.Equal("2:provider-map:ALPHA", lease.MapKeys[0]);
        AssertEx.Equal("2:provider-map:ZETA", lease.MapKeys[1]);
    }

    [Test]
    public async Task MutationWait_CancellationLeaksNoReservation()
    {
        var domain = new KeyedCompositeLockDomain();
        var ownerAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = HoldMutationUntilReleasedAsync(domain,
            ["0:model:ALPHA", "1:path:a.gguf"],
            ownerAcquired,
            releaseOwner.Task);
        await ownerAcquired.Task;
        using var cancellation = new CancellationTokenSource();
        var waiting = WaitForMutationAsync(domain, cancellation.Token);
        await cancellation.CancelAsync();
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => waiting);

        releaseOwner.SetResult();
        await owner;
        var acquired = await AcquireMutationAsync(domain, "1:path:a.gguf");
        await acquired.DisposeAsync();
    }

    [Test]
    public async Task CompositeReadsCoexistAndBlockOverlappingMutation()
    {
        var domain = new KeyedCompositeLockDomain();
        var releaseReaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = HoldReadUntilReleasedAsync(domain, ["0:model:ALPHA", "1:path:a.gguf"], firstAcquired, releaseReaders.Task);
        var second = HoldReadUntilReleasedAsync(domain, ["1:path:a.gguf"], secondAcquired, releaseReaders.Task);
        await Task.WhenAll(firstAcquired.Task, secondAcquired.Task).WaitAsync(TimeSpan.FromSeconds(30));
        var writerTask = AcquireAndReleaseMutationAsync(domain, "1:path:a.gguf");

        await Task.Delay(50);
        AssertEx.False(writerTask.IsCompleted, "An overlapping mutation must wait for every read lease.");
        releaseReaders.SetResult();
        await Task.WhenAll(first, second);
        await writerTask.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task SameFlowNestedAcquisitionIsRejected()
    {
        var domain = new KeyedCompositeLockDomain();
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => AcquireNestedAsync(domain));
    }

    [Test]
    public async Task SiblingLogicalFlows_DoNotShareReentrancyOwnership()
    {
        var domain = new KeyedCompositeLockDomain();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = HoldReadUntilReleasedAsync(domain, ["0:model:ALPHA"], firstAcquired, release.Task);
        var second = HoldReadUntilReleasedAsync(domain, ["0:model:BETA"], secondAcquired, release.Task);

        await Task.WhenAll(firstAcquired.Task, secondAcquired.Task).WaitAsync(TimeSpan.FromSeconds(30));

        // The property under test: the second flow acquired its OWN read lease while the first still holds one.
        // Ownership is per-logical-flow, so the sibling is not rejected the way SameFlowNestedAcquisitionIsRejected
        // proves a nested acquisition inside a single flow is.
        AssertEx.False(first.IsCompleted, "The first sibling flow must still hold its read lease when the second acquires.");
        AssertEx.False(second.IsCompleted, "The second sibling flow must still hold its read lease when the first does.");

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        AssertEx.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully,
            "Neither sibling flow may fault: both must acquire and release their own read lease.");
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
        var identity = resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Import,
            new string('A', 90),
            "F16"));

        AssertEx.False(identity.FinalFileName.Contains("..", StringComparison.Ordinal));
        AssertEx.Equal(expected: 72, identity.FinalFileName[..identity.FinalFileName.IndexOf("-F16-", StringComparison.Ordinal)].Length);
    }

    [Test]
    public void DeterministicIdentity_ProjectorMetadataIsDownloadOnlyAndProducesReservedPath()
    {
        var resolver = CreateIdentityResolver();
        var projector = new GgufProjectorAcquisitionMetadata("mmproj-model.gguf", new string('a', 64), DeclaredSizeBytes: 42);
        var download = resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Download,
            "foo",
            "Q4_K_M",
            projector));

        AssertEx.Equal("foo-projector-849525de9efce6742c0cf2b6.gguf", download.ProjectorRelativePath);
        _ = AssertEx.Throws<ArgumentException>(() => resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Import,
            "foo",
            "Q4_K_M",
            projector)));
    }

    [Test]
    public void RelativePathKeys_CollapseEquivalentSeparatorsAndTrailingSlash()
    {
        var canonical = ModelCoordinationKeys.NormalizeRelativePath("nested/model.gguf");

        AssertEx.Equal(canonical, ModelCoordinationKeys.NormalizeRelativePath("nested//model.gguf/"));
        AssertEx.Equal(canonical, ModelCoordinationKeys.NormalizeRelativePath(@"nested\\model.gguf"));
    }

    private static GgufAcquisitionIdentityResolver CreateIdentityResolver()
    {
        return new GgufAcquisitionIdentityResolver(new ModelNameValidator(Options.Create(new SecurityOptions())));
    }

    private static async Task<ModelCoordinationLockLease> AcquireMutationAsync(KeyedCompositeLockDomain domain, string key)
    {
        return await domain.AcquireMutationAsync([key]);
    }

    private static async Task<ModelCoordinationLockLease> WaitForMutationAsync(KeyedCompositeLockDomain domain, CancellationToken cancellationToken)
    {
        return await domain.AcquireMutationAsync(["1:path:a.gguf", "2:provider-map:ALPHA"], cancellationToken);
    }

    private static async Task AcquireAndReleaseMutationAsync(KeyedCompositeLockDomain domain, string key)
    {
        await using var lease = await domain.AcquireMutationAsync([key]);
    }

    private static async Task AcquireNestedAsync(KeyedCompositeLockDomain domain)
    {
        await using var lease = await domain.AcquireReadAsync(["2:provider-map:ALPHA"]);
        _ = await domain.AcquireReadAsync(["2:provider-map:BETA"]);
    }

    private static async Task HoldReadUntilReleasedAsync(KeyedCompositeLockDomain domain,
        IReadOnlyList<string> keys,
        TaskCompletionSource acquired,
        Task release)
    {
        await using var lease = await domain.AcquireReadAsync(keys);
        acquired.SetResult();
        await release;
    }

    private static async Task HoldMutationUntilReleasedAsync(KeyedCompositeLockDomain domain,
        IReadOnlyList<string> keys,
        TaskCompletionSource acquired,
        Task release)
    {
        await using var lease = await domain.AcquireMutationAsync(keys);
        acquired.SetResult();
        await release;
    }
}
