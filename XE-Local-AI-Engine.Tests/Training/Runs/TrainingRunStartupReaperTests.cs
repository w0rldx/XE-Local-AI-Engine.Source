namespace XE_Local_AI_Engine.Tests.Training.Runs;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with
            {
                Pgid = 4243
            }),
            "A different process group means the pid was reused by an unrelated session.");

    [Test]
    public void Matches_WhenTheStartTimeDiffers_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with
            {
                StartTicks = 987654322
            }),
            "The start time is the pid-reuse guard: a different one is a different process wearing the same pid.");

    [Test]
    public void Matches_WhenTheExecutableDiffers_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with
            {
                ExecutablePath = "/usr/bin/python3"
            }),
            "A different executable is not this run's trainer, whatever else agrees.");

    [Test]
    public void Matches_WhenTheExecutableIsUnreadable_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with
            {
                ExecutablePath = null
            }),
            "An unreadable /proc/[pid]/exe cannot confirm identity, so it must not be treated as confirming it.");

    [Test]
    public void Matches_WhenTheRunTokenDiffers_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with
            {
                RunToken = "00000000000000000000000000000000"
            }),
            "The run token is the one field a recycled pid running the same interpreter cannot forge.");

    [Test]
    public void Matches_WhenTheRunTokenIsAbsent_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt, LiveFacts() with
            {
                RunToken = null
            }),
            "A child whose environment carries no token is not one this host launched.");

    [Test]
    public async Task Reap_WhenAnyReceiptFieldMismatches_NeverSignalsButClearsTheReceiptAndRecovers()
    {
        foreach (var mismatch in Mismatches())
        {
            var (reaper, inspector, store, runId) = Build(mismatch);

            await reaper.StartAsync(CancellationToken.None);

            AssertEx.Equal(expected: 0, inspector.SignalledGroups.Count, "A mismatched receipt must never reach a kill.");
            // A non-match means the pid is dead or recycled, so nothing is left to identify — clearing is the safe
            // direction here, and it is the reaper that does it, not recovery.
            await store.Received(1).SetLaunchReceiptAsync(runId, Arg.Is<ReadOnlyMemory<byte>?>(static value => !value.HasValue),
                Arg.Any<CancellationToken>());
            _ = await store.Received(1).RecoverOnStartupAsync(Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task Reap_WhenEveryReceiptFieldMatches_SignalsTheRecordedProcessGroupThenClearsTheReceipt()
    {
        var (reaper, inspector, store, runId) = Build(LiveFacts());

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, inspector.SignalledGroups.Count, "A fully matching receipt is an orphan this host must reap.");
        AssertEx.Equal(Receipt.Pgid, inspector.SignalledGroups[0]);
        await store.Received(1).SetLaunchReceiptAsync(runId, Arg.Is<ReadOnlyMemory<byte>?>(static value => !value.HasValue),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The sweep reads receipts through a dedicated unpaged query, never a page of the run list. A trainer that
    ///     outlived its host while 200+ newer runs piled up behind it would otherwise fall off the first page — and
    ///     since only the reaper clears a receipt, that orphan would keep its GPU allocation and its anonymity forever.
    /// </summary>
    [Test]
    public async Task Reap_WhenMoreReceiptsExistThanOnePageHolds_InspectsAndClearsEveryOne()
    {
        const int count = 250;
        var runIds = Enumerable.Range(0, count).Select(static _ => Guid.NewGuid()).ToArray();
        var store = StoreWith([.. runIds.Select(static id => new TrainingRunLaunchReceipt(id, Serialize(Receipt)))]);
        var inspector = new FakeTrainingProcessInspector(LiveFacts());

        await Reaper(store, inspector).StartAsync(CancellationToken.None);

        AssertEx.Equal(count, inspector.SignalledGroups.Count, "Every recorded receipt must be inspected, whatever its run's age.");
        await store.Received(count).SetLaunchReceiptAsync(Arg.Any<Guid>(), Arg.Is<ReadOnlyMemory<byte>?>(static value => !value.HasValue),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A receipt is the only handle on a trainer this host has no process handle for. If the inspection or the kill
    ///     throws, the process may well still be alive — so that receipt has to survive to the next startup, while the
    ///     rest of the sweep carries on and node startup is unaffected.
    /// </summary>
    [Test]
    public async Task Reap_WhenInspectingOneReceiptThrows_KeepsThatReceiptAndStillProcessesTheOthers()
    {
        var (firstId, secondId, thirdId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var store = StoreWith([
            new TrainingRunLaunchReceipt(firstId, Serialize(Receipt with
            {
                Pid = 1
            })),
            new TrainingRunLaunchReceipt(secondId, Serialize(Receipt with
            {
                Pid = 2
            })),
            new TrainingRunLaunchReceipt(thirdId, Serialize(Receipt with
            {
                Pid = 3
            }))
        ]);

        var inspector = Substitute.For<ITrainingProcessInspector>();
        _ = inspector.Inspect(2).Returns(static _ => throw new IOException("/proc went away mid-read."));

        await Reaper(store, inspector).StartAsync(CancellationToken.None);

        await store.DidNotReceive().SetLaunchReceiptAsync(secondId, Arg.Any<ReadOnlyMemory<byte>?>(), Arg.Any<CancellationToken>());
        await store.Received(1).SetLaunchReceiptAsync(firstId, Arg.Is<ReadOnlyMemory<byte>?>(static value => !value.HasValue),
            Arg.Any<CancellationToken>());
        await store.Received(1).SetLaunchReceiptAsync(thirdId, Arg.Is<ReadOnlyMemory<byte>?>(static value => !value.HasValue),
            Arg.Any<CancellationToken>());
        _ = await store.Received(1).RecoverOnStartupAsync(Arg.Any<CancellationToken>());
    }

    private static IEnumerable<TrainingProcessFacts?> Mismatches() =>
    [
        null,
        LiveFacts() with
        {
            Pgid = 4243
        },
        LiveFacts() with
        {
            StartTicks = 1
        },
        LiveFacts() with
        {
            ExecutablePath = "/usr/bin/python3"
        },
        LiveFacts() with
        {
            ExecutablePath = null
        },
        LiveFacts() with
        {
            RunToken = "different"
        },
        LiveFacts() with
        {
            RunToken = null
        }
    ];

    private static ReadOnlyMemory<byte> Serialize(TrainingLaunchReceiptV1 receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(receipt, TrainingJson.Options);

    private static ITrainingRunStore StoreWith(IReadOnlyList<TrainingRunLaunchReceipt> receipts)
    {
        var store = Substitute.For<ITrainingRunStore>();
        _ = store.ListLaunchReceiptsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TrainingRunLaunchReceipt>>(receipts);
        _ = store.RecoverOnStartupAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        return store;
    }

    private static TrainingRunStartupReaper Reaper(ITrainingRunStore store, ITrainingProcessInspector inspector)
    {
        var services = new ServiceCollection();
        _ = services.AddScoped(_ => store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var keyHolder = new FixedNodeSqliteKeyHolder(new byte[32]);
        var workspace = new TrainingRunWorkspace(new FixedNodeDataDirectory(root), keyHolder);
        return new TrainingRunStartupReaper(scopeFactory, inspector, workspace, TimeProvider.System, NullLogger<TrainingRunStartupReaper>.Instance);
    }

    private static (TrainingRunStartupReaper Reaper, FakeTrainingProcessInspector Inspector, ITrainingRunStore Store, Guid RunId) Build(TrainingProcessFacts? facts)
    {
        var runId = Guid.NewGuid();
        var store = StoreWith([new TrainingRunLaunchReceipt(runId, Serialize(Receipt))]);
        var inspector = new FakeTrainingProcessInspector(facts);
        return (Reaper(store, inspector), inspector, store, runId);
    }

    [Test]
    public void Matches_WhenTheRecordedTokenIsEmpty_IsFalse() =>
        AssertEx.False(TrainingRunStartupReaper.Matches(Receipt with
            {
                RunToken = string.Empty
            }, LiveFacts() with
            {
                RunToken = string.Empty
            }),
            "Two empty tokens must not be read as agreement — that would make an unset token a universal match.");
}
