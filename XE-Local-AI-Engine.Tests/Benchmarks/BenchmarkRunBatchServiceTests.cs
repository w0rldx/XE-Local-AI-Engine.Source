namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkRunBatchServiceTests
{
    private static readonly Guid ProjectId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Test]
    public async Task StartAsync_WholeBatchFailureBeforeAStart_ThrowsAndDoesNotAttemptLaterCells()
    {
        var freeze = Substitute.For<IBenchmarkRunFreezeService>();
        freeze.StartAsync(Request("model-a", 4), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>())
              .Returns<IReadOnlyList<BenchmarkRunRecord>>(_ => throw new BenchmarkConflictException("VersionConflict"));
        var service = new BenchmarkRunBatchService(freeze, TimeProvider.System);

        _ = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => service.StartAsync(Batch("model-a", "model-b"))).ConfigureAwait(false);

        _ = freeze.DidNotReceive().StartAsync(Arg.Is<BenchmarkRunStartRequest>(request => request.PrimaryModelName == "model-b"),
            Arg.Any<BenchmarkFreezeScope?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_WholeBatchFailureAfterAStart_StopsAndPreservesStartedIdsWithoutDuplicates()
    {
        var freeze = Substitute.For<IBenchmarkRunFreezeService>();
        freeze.StartAsync(Request("model-a", 4), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>()).Returns([Run()]);
        freeze.StartAsync(Request("model-b", 5), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>())
              .Returns<IReadOnlyList<BenchmarkRunRecord>>(_ => throw new BenchmarkConflictException("VersionConflict"));
        var service = new BenchmarkRunBatchService(freeze, TimeProvider.System);

        var result = await service.StartAsync(Batch("model-a", "model-b", "model-c")).ConfigureAwait(false);

        AssertEx.Equal(expected: 5L, result.ProjectVersion);
        AssertEx.Equal(expected: 1, result.Started.Count);
        AssertEx.Equal(Run().Id, result.Started[0].RunIds[0]);
        AssertEx.Equal(expected: 2, result.Rejected.Count);
        AssertEx.Equal("model-b", result.Rejected[0].ModelName);
        AssertEx.Equal(BenchmarkRunBatchRejectionKind.Failure, result.Rejected[0].Kind);
        AssertEx.Equal("model-c", result.Rejected[1].ModelName);
        AssertEx.Equal(BenchmarkRunBatchRejectionKind.NotAttempted, result.Rejected[1].Kind);
        _ = freeze.Received(1).StartAsync(Request("model-a", 4), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>());
        _ = freeze.Received(1).StartAsync(Request("model-b", 5), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>());
        _ = freeze.DidNotReceive().StartAsync(Arg.Is<BenchmarkRunStartRequest>(request => request.PrimaryModelName == "model-c"),
            Arg.Any<BenchmarkFreezeScope?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartAsync_PerCellFailuresRemainInInputOrderAndDoNotConsumeProjectVersions()
    {
        var freeze = Substitute.For<IBenchmarkRunFreezeService>();
        freeze.StartAsync(Request("unsupported", 4), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>())
              .Returns<IReadOnlyList<BenchmarkRunRecord>>(_ => throw new NotSupportedException("Unsupported snapshot."));
        freeze.StartAsync(Request("good", 4), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>()).Returns([Run()]);
        var service = new BenchmarkRunBatchService(freeze, TimeProvider.System);
        var request = new BenchmarkRunBatchRequest(ProjectId,
            4,
            [new BenchmarkRunBatchItem(" ", null), new BenchmarkRunBatchItem("unsupported", null), new BenchmarkRunBatchItem("good", null)],
            1,
            false,
            BenchmarkRepeatMode.Throughput,
            null);

        var result = await service.StartAsync(request).ConfigureAwait(false);

        AssertEx.Equal(expected: 5L, result.ProjectVersion);
        AssertEx.Equal(expected: 2, result.Rejected.Count);
        AssertEx.Equal(" ", result.Rejected[0].ModelName);
        AssertEx.True(result.Rejected[0].Failure is BenchmarkValidationException);
        AssertEx.Equal("unsupported", result.Rejected[1].ModelName);
        AssertEx.True(result.Rejected[1].Failure is NotSupportedException);
        AssertEx.Equal("good", result.Started[0].ModelName);
        _ = freeze.Received(1).StartAsync(Request("good", 4), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>());
    }

    private static BenchmarkRunBatchRequest Batch(params string[] modelNames) =>
        new(ProjectId,
            4,
            [.. modelNames.Select(static name => new BenchmarkRunBatchItem(name, null))],
            1,
            false,
            BenchmarkRepeatMode.Throughput,
            null);

    private static BenchmarkRunStartRequest Request(string modelName, long expectedVersion) =>
        new(ProjectId, modelName, expectedVersion, null, 1, false);

    private static BenchmarkRunRecord Run() =>
        new(Guid.Parse("20000000-0000-0000-0000-000000000002"),
            ProjectId,
            Encoding.UTF8.GetBytes("{}"),
            "model",
            LocalModelOrigin.Imported,
            "fingerprint",
            "agent",
            1,
            4096,
            BenchmarkPrimaryStatus.Queued,
            null,
            null,
            null,
            null,
            null,
            0,
            null,
            null,
            1,
            1,
            null,
            null,
            1);
}
