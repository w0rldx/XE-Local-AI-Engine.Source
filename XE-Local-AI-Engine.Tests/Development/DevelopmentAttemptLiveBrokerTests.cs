namespace XE_Local_AI_Engine.Tests.Development;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevelopmentAttemptLiveBrokerTests
{
    [Test]
    public async Task TryPublish_WhenReplaceableChannelIsFull_RemainsNonBlockingAndRetainsLatestSnapshot()
    {
        var attemptId = Guid.NewGuid();
        var broker = CreateBroker(capacity: 2);
        AssertEx.True(broker.Register(attemptId));

        AssertEx.True(broker.TryPublish(Update(attemptId, DevelopmentAttemptLiveUpdateKind.Metrics, "first")));
        AssertEx.True(broker.TryPublish(Update(attemptId, DevelopmentAttemptLiveUpdateKind.Metrics, "second")));
        AssertEx.False(broker.TryPublish(Update(attemptId, DevelopmentAttemptLiveUpdateKind.Metrics, "latest")));

        AssertEx.True(broker.TryGetSnapshot(attemptId, out var snapshot));
        AssertEx.Equal(3L, snapshot.Watermark);
        AssertEx.Equal(1L, snapshot.DroppedOrCoalescedUpdateCount);
        AssertEx.Equal("latest", AssertEx.NotNull(snapshot.Latest).CurrentActivity);
        AssertEx.True(broker.TryGetDeliveryReader(attemptId, out var reader));
        var deliveryReader = AssertEx.NotNull(reader);
        AssertEx.Equal(1L, (await deliveryReader.ReadAsync()).Sequence);
        AssertEx.Equal(2L, (await deliveryReader.ReadAsync()).Sequence);
    }

    [Test]
    public async Task TryPublish_WhenWarningArrivesAtCapacity_EvictsReplaceableUpdateInsteadOfLosingWarning()
    {
        var attemptId = Guid.NewGuid();
        var broker = CreateBroker(capacity: 2);
        AssertEx.True(broker.Register(attemptId));
        _ = broker.TryPublish(Update(attemptId, DevelopmentAttemptLiveUpdateKind.Metrics, "first"));
        _ = broker.TryPublish(Update(attemptId, DevelopmentAttemptLiveUpdateKind.Metrics, "second"));

        AssertEx.True(broker.TryPublish(Update(attemptId, DevelopmentAttemptLiveUpdateKind.Warning, "warning") with
        {
            WarningCategory = DevelopmentProgressWarningCategory.RepeatedTool,
            WarningMessage = "Repeated tool"
        }));

        AssertEx.True(broker.TryGetDeliveryReader(attemptId, out var reader));
        var deliveryReader = AssertEx.NotNull(reader);
        AssertEx.Equal(2L, (await deliveryReader.ReadAsync()).Sequence);
        var warning = await deliveryReader.ReadAsync();
        AssertEx.Equal(3L, warning.Sequence);
        AssertEx.Equal(DevelopmentAttemptLiveUpdateKind.Warning, warning.Kind);
    }

    [Test]
    public void TryPublish_SanitizesCredentialsAbsolutePathsAndIdentifierPayloads()
    {
        var attemptId = Guid.NewGuid();
        var broker = CreateBroker(capacity: 2);
        AssertEx.True(broker.Register(attemptId));
        var published = broker.TryPublish(Update(attemptId, DevelopmentAttemptLiveUpdateKind.Output,
                "password=!Sensitive12345678 from /home/operator/private/source.cs") with
            {
                ModelId = "model\nsecret",
                CurrentToolId = "read_file raw arguments"
            });

        AssertEx.True(published);
        AssertEx.True(broker.TryGetSnapshot(attemptId, out var snapshot));
        var latest = AssertEx.NotNull(snapshot.Latest);
        AssertEx.False(latest.CurrentActivity!.Contains("!Sensitive12345678", StringComparison.Ordinal));
        AssertEx.False(latest.CurrentActivity.Contains("/home/operator", StringComparison.Ordinal));
        AssertEx.False(latest.ModelId.Contains('\n', StringComparison.Ordinal));
        AssertEx.False(latest.CurrentToolId!.Contains(' ', StringComparison.Ordinal));
    }

    [Test]
    public void Complete_ReleasesAttemptStateAndIsIdempotent()
    {
        var attemptId = Guid.NewGuid();
        var broker = CreateBroker(capacity: 2);
        AssertEx.True(broker.Register(attemptId));
        _ = broker.TryPublish(Update(attemptId, DevelopmentAttemptLiveUpdateKind.Activity, "working"));

        AssertEx.True(broker.Complete(attemptId));
        AssertEx.False(broker.Complete(attemptId));
        AssertEx.False(broker.TryGetSnapshot(attemptId, out _));
        AssertEx.False(broker.TryGetDeliveryReader(attemptId, out _));
        AssertEx.False(broker.TryPublish(Update(attemptId, DevelopmentAttemptLiveUpdateKind.Activity, "late")));
    }

    private static DevelopmentAttemptLiveBroker CreateBroker(int capacity) =>
        new(Options.Create(new DevelopmentOptions
            {
                LiveChannelCapacity = capacity,
                MaxLiveTextCharacters = 512
            }),
            TimeProvider.System);

    private static DevelopmentAttemptLiveUpdate Update(Guid attemptId,
        DevelopmentAttemptLiveUpdateKind kind,
        string activity) =>
        new()
        {
            ProjectId = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            AttemptId = attemptId,
            Kind = kind,
            Role = DevelopmentAttemptRole.Coder,
            Status = Client.Persistence.Entities.DevelopmentAttemptStatus.Running,
            ModelId = "local-model",
            Provider = "local",
            CurrentActivity = activity
        };
}
