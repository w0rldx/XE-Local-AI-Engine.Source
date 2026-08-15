namespace XE_Local_AI_Engine.Tests.Training.Runs;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Receipt validation is the whole safety story for reaping a process this host does not own a handle to. A pid is
///     recycled freely, and the trainer's executable is the shared venv interpreter — so a path match, or even a
///     pid-plus-path match, would happily kill an unrelated Python. Every recorded field is a gate, and each one is
///     pinned individually here because the guarantee is per-field.
/// </summary>
public sealed class TrainingRunStartupReaperTests
{
    private static readonly TrainingLaunchReceiptV1 Receipt = new()
    {
        Pid = 4242,
        Pgid = 4242,
        ExecutablePath = "/home/user/.local/share/XE-Local-AI-Engine/training-runtime/venv/active/.venv/bin/python",
        StartTicks = 987654321,
        RunToken = "0f3c1a2b4d5e6f708192a3b4c5d6e7f8"
    };

    private static TrainingProcessFacts LiveFacts() =>
        new(Receipt.Pgid, Receipt.StartTicks, Receipt.ExecutablePath, Receipt.RunToken);

    [Test]
    public void Matches_WhenEveryFieldAgrees_IsTrue() =>
        AssertEx.True(TrainingRunStartupReaper.Matches(Receipt, LiveFacts()),
            "A fully matching receipt is the only case that may be signalled.");

    [Test]
    public void Matches_WhenTheProcessIsGone_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, facts: null),
            "An unreadable or absent /proc entry means the process is gone; there is nothing to signal.");

    [Test]
    public void Matches_WhenTheProcessGroupDiffers_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with { Pgid = 4243 }),
            "A different process group means the pid was reused by an unrelated session.");

    [Test]
    public void Matches_WhenTheStartTimeDiffers_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with { StartTicks = 987654322 }),
            "The start time is the pid-reuse guard: a different one is a different process wearing the same pid.");

    [Test]
    public void Matches_WhenTheExecutableDiffers_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with { ExecutablePath = "/usr/bin/python3" }),
            "A different executable is not this run's trainer, whatever else agrees.");

    [Test]
    public void Matches_WhenTheExecutableIsUnreadable_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with { ExecutablePath = null }),
            "An unreadable /proc/[pid]/exe cannot confirm identity, so it must not be treated as confirming it.");

    [Test]
    public void Matches_WhenTheRunTokenDiffers_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with { RunToken = "00000000000000000000000000000000" }),
            "The run token is the one field a recycled pid running the same interpreter cannot forge.");

    [Test]
    public void Matches_WhenTheRunTokenIsAbsent_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with { RunToken = null }),
            "A child whose environment carries no token is not one this host launched.");

    [Test]
    public async Task Reap_WhenAnyReceiptFieldMismatches_NeverSignalsButStillRecovers()
    {
        foreach (var mismatch in Mismatches())
        {
            var (reaper, inspector, store) = Build(mismatch);

            await reaper.StartAsync(CancellationToken.None);

            AssertEx.Equal(expected: 0, inspector.SignalledGroups.Count, "A mismatched receipt must never reach a kill.");
            // Recovery still has to run: the interrupted run is terminalized and its stale receipt cleared, which is
            // what stops a later sweep acting on the same recycled pid.
            _ = await store.Received(1).RecoverOnStartupAsync(Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task Reap_WhenEveryReceiptFieldMatches_SignalsTheRecordedProcessGroup()
    {
        var (reaper, inspector, _) = Build(LiveFacts());

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, inspector.SignalledGroups.Count, "A fully matching receipt is an orphan this host must reap.");
        AssertEx.Equal(Receipt.Pgid, inspector.SignalledGroups[0]);
    }

    private static IEnumerable<TrainingProcessFacts?> Mismatches() =>
    [
        null,
        LiveFacts() with { Pgid = 4243 },
        LiveFacts() with { StartTicks = 1 },
        LiveFacts() with { ExecutablePath = "/usr/bin/python3" },
        LiveFacts() with { ExecutablePath = null },
        LiveFacts() with { RunToken = "different" },
        LiveFacts() with { RunToken = null }
    ];

    private static (TrainingRunStartupReaper Reaper, FakeTrainingProcessInspector Inspector, ITrainingRunStore Store) Build(
        TrainingProcessFacts? facts)
    {
        var store = Substitute.For<ITrainingRunStore>();
        _ = store.ListAsync(Arg.Any<TrainingRunQuery>(), Arg.Any<CancellationToken>())
                 .Returns(new TrainingRunPage([Run()], TotalCount: 1));
        _ = store.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);

        var services = new ServiceCollection();
        _ = services.AddScoped(_ => store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var inspector = new FakeTrainingProcessInspector(facts);
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var keyHolder = new FixedNodeSqliteKeyHolder(new byte[32]);
        var workspace = new TrainingRunWorkspace(new FixedNodeDataDirectory(root), keyHolder);
        return (new TrainingRunStartupReaper(scopeFactory, inspector, workspace, TimeProvider.System, NullLogger<TrainingRunStartupReaper>.Instance),
            inspector,
            store);
    }

    private static TrainingRunRecord Run() =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            "v1:abc",
            DatasetRevision: 1,
            ReadOnlyMemory<byte>.Empty,
            Guid.NewGuid(),
            LinkedInstalledModelName: null,
            LinkedModelContentFingerprint: null,
            ReadOnlyMemory<byte>.Empty,
            LicenseConfirmationJson: null,
            TrainingRunStatus.Training,
            ProgressJson: null,
            LogTail: null,
            JsonSerializer.SerializeToUtf8Bytes(Receipt, TrainingJson.Options),
            ErrorMessage: null,
            Version: 2,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            TrainingWorkStatus.Running,
            WorkErrorMessage: null);

    [Test]
    public void Matches_WhenTheRecordedTokenIsEmpty_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt with { RunToken = string.Empty }, LiveFacts() with { RunToken = string.Empty }),
            "Two empty tokens must not be read as agreement — that would make an unset token a universal match.");
}
