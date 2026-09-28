namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ProcessLaunchAdmissionRegistryTests
{
    private const string Model = "model/a";

    [Test]
    public void AcquireBeginConsumerFirst_LeavesOrphanBlockerUntilLaunchCompletes()
    {
        var registry = new ProcessLaunchAdmissionRegistry();
        var admission = Admission(Model);

        AssertEx.True(registry.TryAcquire(admission, out var consumer));
        var pending = registry.Snapshot(Model, ModelRole.Chat);
        AssertEx.True(pending.HasRequestedKey);
        AssertEx.Contains(pending.AdmittedKeys, new ProcessLaunchAdmissionKey(Model, ModelRole.Chat));
        AssertEx.False(registry.TryAcquire(admission, out _));
        AssertEx.True(registry.TryBeginLaunch(Model, ModelRole.Chat, out var captured, out var ticket));
        AssertEx.Equal(admission, captured);

        consumer!.Dispose();
        consumer.Dispose();
        AssertEx.True(registry.Snapshot("model/b", ModelRole.Chat).HasGlobalBlocker);
        AssertEx.False(registry.TryAcquire(Admission("model/b"), out _));

        ticket!.Dispose();
        ticket.Dispose();
        AssertEx.False(registry.Snapshot(Model, ModelRole.Chat).HasRequestedKey);
        AssertEx.True(registry.TryAcquire(Admission("model/b"), out var next));
        next!.Dispose();
    }

    [Test]
    public void LaunchFirstThenConsumer_ReleasesOnlyAfterBothOwnersDispose()
    {
        var registry = new ProcessLaunchAdmissionRegistry();
        AssertEx.True(registry.TryAcquire(Admission(Model), out var consumer));
        AssertEx.True(registry.TryBeginLaunch(Model, ModelRole.Chat, out _, out var ticket));

        ticket!.Dispose();
        AssertEx.True(registry.Snapshot(Model, ModelRole.Chat).HasRequestedKey);
        consumer!.Dispose();
        AssertEx.False(registry.Snapshot(Model, ModelRole.Chat).HasRequestedKey);
    }

    [Test]
    public void UnboundLaunch_BlocksAdmissionsUntilTicketCompletes()
    {
        var registry = new ProcessLaunchAdmissionRegistry();
        AssertEx.True(registry.TryBeginLaunch(Model, ModelRole.Chat, out var admission, out var ticket));
        AssertEx.Null(admission);
        AssertEx.True(registry.Snapshot("model/b", ModelRole.Chat).HasGlobalBlocker);
        AssertEx.False(registry.TryAcquire(Admission("model/b"), out _));

        ticket!.Dispose();
        AssertEx.True(registry.TryAcquire(Admission("model/b"), out var consumer));
        consumer!.Dispose();
    }

    [Test]
    public void AdmittedKey_BlocksDifferentUnboundLaunchUntilConsumerDisposes()
    {
        var registry = new ProcessLaunchAdmissionRegistry();
        AssertEx.True(registry.TryAcquire(Admission(Model), out var consumer));

        IProcessLaunchTicket? blockedTicket = null;
        try
        {
            AssertEx.False(registry.TryBeginLaunch("model/b", ModelRole.Chat, out var blockedAdmission, out blockedTicket));
            AssertEx.Null(blockedAdmission);
            AssertEx.Null(blockedTicket);
        }
        finally
        {
            blockedTicket?.Dispose();
        }

        consumer!.Dispose();
        AssertEx.True(registry.TryBeginLaunch("model/b", ModelRole.Chat, out var unboundAdmission, out var unboundTicket));
        AssertEx.Null(unboundAdmission);
        unboundTicket!.Dispose();
    }

    [Test]
    public void AdmittedPooledKey_CoexistsWithAnAdmittedChatKey()
    {
        // A pooled spawn that passed capacity is bound like a chat one, so neither blocks the other; only unbound launches do.
        var registry = new ProcessLaunchAdmissionRegistry();
        var chat = Admission(Model);
        var reranker = Admission("bge-reranker", ModelRole.Reranker);
        AssertEx.True(registry.TryAcquire(chat, out var chatConsumer));
        AssertEx.True(registry.TryAcquire(reranker, out var rerankerConsumer), "An admitted chat key must not block a pooled admission.");

        AssertEx.True(registry.TryBeginLaunch("bge-reranker", ModelRole.Reranker, out var rerankerCaptured, out var rerankerTicket));
        AssertEx.Equal(reranker, rerankerCaptured);
        AssertEx.True(registry.TryBeginLaunch(Model, ModelRole.Chat, out var chatCaptured, out var chatTicket));
        AssertEx.Equal(chat, chatCaptured);
        AssertEx.False(registry.Snapshot("model/b", ModelRole.Chat).HasGlobalBlocker, "Two bound launches leave no global blocker.");

        rerankerTicket!.Dispose();
        rerankerConsumer!.Dispose();
        chatTicket!.Dispose();
        chatConsumer!.Dispose();
        AssertEx.Empty(registry.Snapshot(Model, ModelRole.Chat).AdmittedKeys.ToArray());
    }

    [Test]
    public void DistinctUnboundLaunches_CanOverlapWithoutExactAdmission()
    {
        var registry = new ProcessLaunchAdmissionRegistry();
        AssertEx.True(registry.TryBeginLaunch(Model, ModelRole.Chat, out var firstAdmission, out var firstTicket));
        AssertEx.Null(firstAdmission);
        AssertEx.True(registry.TryBeginLaunch("model/b", ModelRole.Chat, out var secondAdmission, out var secondTicket));
        AssertEx.Null(secondAdmission);

        firstTicket!.Dispose();
        secondTicket!.Dispose();
    }

    [Test]
    public void AdmittedModelIdentity_IsCaseInsensitiveAcrossCapacityAndLaunch()
    {
        var registry = new ProcessLaunchAdmissionRegistry();
        var admission = Admission("Model/A");
        AssertEx.True(registry.TryAcquire(admission, out var consumer));

        AssertEx.True(registry.Snapshot("model/a", ModelRole.Chat).HasRequestedKey);
        AssertEx.True(registry.TryBeginLaunch("model/a", ModelRole.Chat, out var captured, out var ticket));
        AssertEx.Equal(admission, captured);

        ticket!.Dispose();
        consumer!.Dispose();
        AssertEx.False(registry.Snapshot("MODEL/A", ModelRole.Chat).HasRequestedKey);
    }

    private static ProcessLaunchAdmission Admission(string modelName, ModelRole role = ModelRole.Chat)
    {
        var allocation = new ProcessContextAllocation
        {
            ProcessContextTokens = 8192,
            ModelTrainContextTokens = 131072,
            Source = ProcessContextAllocationSource.HardwareTier,
            Placement = ProcessPlacementMode.GpuResident,
            Footprint = ResourceFootprint.Zero,
            ContentIdentity = $"{modelName}:0",
            CacheKey = $"cache:{modelName}"
        };
        return new ProcessLaunchAdmission
        {
            ModelName = modelName,
            Role = role,
            Variant = GpuVariant.Cuda,
            ResolvedArguments = ResolvedLaunchArguments.Explore(),
            Allocation = allocation
        };
    }
}
