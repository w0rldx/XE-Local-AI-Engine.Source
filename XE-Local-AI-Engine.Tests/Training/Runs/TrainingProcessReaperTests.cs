namespace XE_Local_AI_Engine.Tests.Training.Runs;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Providers.Training;
using XE_Local_AI_Engine.Tests.Testing;
using static Providers.Training.LinuxTrainingProcessGroupTests;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The reap outcome decides whether a warm worker's receipt file may be deleted, so only a process proven gone is
///     <see cref="TrainingReapOutcome.Verified" />. The row path ignores the outcome and keeps clearing as before.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class TrainingProcessReaperTests
{
    private static readonly TrainingLaunchReceiptV1 Receipt = new()
    {
        Pid = 4242,
        Pgid = 4242,
        ExecutablePath = "/venv/bin/python",
        StartTicks = 987654321,
        RunToken = "0f3c1a2b4d5e6f708192a3b4c5d6e7f8"
    };

    private static readonly TrainingProcessFacts LiveFacts = new()
    {
        Pgid = Receipt.Pgid,
        StartTicks = Receipt.StartTicks,
        ExecutablePath = Receipt.ExecutablePath,
        RunToken = Receipt.RunToken
    };

    [Test]
    public async Task Reap_WhenThereIsNoReceipt_IsVerified()
    {
        var reap = await ReapAsync(new FakeTrainingProcessInspector(LiveFacts), receiptJson: null);

        AssertEx.Equal(TrainingReapOutcome.Verified, reap.Outcome, "No receipt describes no process.");
        AssertEx.True(reap.Cleared);
    }

    [Test]
    [Arguments("")]
    [Arguments("{not json")]
    public async Task Reap_WhenTheReceiptCannotBeParsed_IsNeverVerifiedNorSignalled_ButStillClearsTheRow(string payload)
    {
        var inspector = new FakeTrainingProcessInspector(LiveFacts);

        var reap = await ReapAsync(inspector, Encoding.UTF8.GetBytes(payload));

        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome, "A receipt that identifies no process is no evidence that its process exited.");
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Empty(inspector.SignalledProcesses);
        AssertEx.True(reap.Cleared, "The row path keeps clearing a receipt it can never match.");
    }

    /// <summary>A receipt that parses but names no process: probing pid 0 reads as gone and a zero start time "differs" from any, so neither may verify.</summary>
    [Test]
    [Arguments("empty-object")]
    [Arguments("pid-0")]
    [Arguments("start-ticks-0")]
    [Arguments("empty-token")]
    public async Task Reap_WhenTheReceiptIsIncomplete_IsNeverVerifiedNorProbed_ButStillClearsTheRow(string incomplete)
    {
        var inspector = Substitute.For<ITrainingProcessInspector>();
        var payload = incomplete switch
        {
            "empty-object" => new ReadOnlyMemory<byte>("{}"u8.ToArray()),
            "pid-0" => Serialize(Receipt with
            {
                Pid = 0
            }),
            "start-ticks-0" => Serialize(Receipt with
            {
                StartTicks = 0
            }),
            _ => Serialize(Receipt with
            {
                RunToken = string.Empty
            })
        };

        var reap = await ReapAsync(inspector, payload);

        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome);
        AssertEx.Empty(inspector.ReceivedCalls(), "Nothing is probed or signalled for a receipt that identifies no process.");
        AssertEx.True(reap.Cleared, "The row path keeps clearing a receipt it can never match.");
    }

    /// <summary>A receipt read before setsid ran records the host's group, yet the trainer leads one named by its pid: that is the group probed.</summary>
    [Test]
    [Arguments(4242)]
    [Arguments(777)]
    public async Task Reap_WhenTheProcessIsGone_AndItsGroupIsEmpty_IsVerifiedWithoutSignalling(int recordedPgid)
    {
        var inspector = new FakeTrainingProcessInspector(facts: null);

        var reap = await ReapAsync(inspector, Serialize(Receipt with
        {
            Pgid = recordedPgid
        }));

        AssertEx.Equal(TrainingReapOutcome.Verified, reap.Outcome);
        AssertEx.True(reap.Cleared);
        AssertEx.Equal(Receipt.Pid, inspector.ProbedGroups.Single(), "The group the trainer leads is named by its pid.");
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Empty(inspector.SignalledProcesses);
    }

    /// <summary>A pid is not reallocated while it names a group, so members left behind by a missing leader are the trainer's.</summary>
    [Test]
    [Arguments(4242)]
    [Arguments(777)]
    public async Task Reap_WhenTheLeaderIsGone_ButItsGroupHasMembers_IsNeverVerifiedNorSignalled_ButStillClearsTheRow(int recordedPgid)
    {
        var inspector = new FakeTrainingProcessInspector(facts: null)
        {
            GroupHasMembers = true
        };

        var reap = await ReapAsync(inspector, Serialize(Receipt with
        {
            Pgid = recordedPgid
        }));

        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome, "A worker the trainer forked may still hold the GPU.");
        AssertEx.Equal(Receipt.Pid, inspector.ProbedGroups.Single());
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Empty(inspector.SignalledProcesses);
        AssertEx.True(reap.Cleared, "The row path keeps clearing as before.");
    }

    [Test]
    public async Task Reap_WhenTheProcEntryIsUnreadable_IsNeverVerifiedButStillClearsTheRow()
    {
        var inspector = new FakeTrainingProcessInspector(facts: null)
        {
            AbsentPresence = TrainingProcessPresence.Unknown
        };

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome, "An unreadable entry may still be a live GPU holder.");
        AssertEx.True(reap.Cleared, "The row path keeps today's behaviour: an unreadable entry still clears the row receipt.");
    }

    /// <summary>A changed start time proves the old group empty too, since its pid could not have been reallocated otherwise: nothing is probed.</summary>
    [Test]
    public async Task Reap_WhenThePidNowBelongsToAnotherProcess_IsVerifiedWithoutSignallingOrProbingTheGroup()
    {
        var inspector = new FakeTrainingProcessInspector(LiveFacts with
        {
            StartTicks = Receipt.StartTicks + 1
        })
        {
            GroupHasMembers = true
        };

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(TrainingReapOutcome.Verified, reap.Outcome);
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Empty(inspector.ProbedGroups, "The new process may lead a group of the same id; that is no member of ours.");
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Reap_WhenTheLivePidsIdentityIsPartlyUnreadable_IsNeverVerifiedNorSignalled(bool exeUnreadable, bool tokenUnreadable)
    {
        var inspector = new FakeTrainingProcessInspector(LiveFacts with
        {
            ExecutablePath = exeUnreadable ? null : LiveFacts.ExecutablePath,
            RunToken = tokenUnreadable ? null : LiveFacts.RunToken
        });

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome, "The pid exists and nothing read contradicts the receipt, so it may still be ours.");
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Empty(inspector.SignalledProcesses);
        AssertEx.True(reap.Cleared, "The row path keeps clearing as before.");
    }

    /// <summary>The same lifetime under another executable path is still our process: a replaced interpreter must never drop a live worker's receipt.</summary>
    [Test]
    [Arguments("/venv/bin/python3.12", "same")]
    [Arguments("/venv/bin/python (deleted)", "same")]
    [Arguments("/venv/bin/python (deleted)", "unreadable")]
    [Arguments("/venv/bin/python (deleted)", "other-group")]
    public async Task Reap_WhenOnlyTheExecutablePathOrGroupDiffers_IsNeverVerifiedNorSignalled(string executablePath, string variant)
    {
        var inspector = new FakeTrainingProcessInspector(LiveFacts with
        {
            ExecutablePath = executablePath,
            RunToken = variant == "unreadable" ? null : LiveFacts.RunToken,
            Pgid = variant == "other-group" ? 777 : LiveFacts.Pgid
        });

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome, "Pid and start time still name our process.");
        AssertEx.Empty(inspector.SignalledGroups, "A path that does not match is not enough to signal.");
        AssertEx.Empty(inspector.SignalledProcesses);
        AssertEx.True(reap.Cleared, "The row path keeps clearing as before.");
    }

    /// <summary>A token read and found different, with the start time unchanged, does not prove the old group empty: it is probed.</summary>
    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task Reap_WhenOnlyTheTokenContradictsTheReceipt_IsVerifiedOnlyOnceThePidsGroupIsEmpty(bool groupHasMembers, bool verified)
    {
        var inspector = new FakeTrainingProcessInspector(LiveFacts with
        {
            ExecutablePath = null,
            RunToken = "someone-else"
        })
        {
            GroupHasMembers = groupHasMembers
        };

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(verified ? TrainingReapOutcome.Verified : TrainingReapOutcome.NotVerified, reap.Outcome);
        AssertEx.Equal(Receipt.Pid, inspector.ProbedGroups.Single());
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Empty(inspector.SignalledProcesses);
        AssertEx.True(reap.Cleared, "The row path keeps clearing as before.");
    }

    [Test]
    [Arguments(TrainingProcessPresence.Gone, true)]
    [Arguments(TrainingProcessPresence.Alive, false)]
    [Arguments(TrainingProcessPresence.Unknown, false)]
    public async Task Reap_WhenTheReceiptMatches_ReportsWhatTheKillVerified(TrainingProcessPresence afterKill, bool verified)
    {
        var inspector = new FakeTrainingProcessInspector(LiveFacts)
        {
            PresenceAfterKill = afterKill
        };

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(verified ? TrainingReapOutcome.Verified : TrainingReapOutcome.NotVerified, reap.Outcome);
        AssertEx.Equal(expected: 1, inspector.SignalledGroups.Count, "A matching receipt is always killed first.");
        AssertEx.True(reap.Cleared, "The row receipt clears whatever the kill verified, as before.");
    }

    /// <summary>The real kill path behind scripted <c>/proc</c> facts: a leader that vanished, or is a zombie, after its SIGTERM is gone only once its group is empty.</summary>
    /// <remarks>A group kill polls within the kill's verify bound; a pid-only kill probes once.</remarks>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments("group", false, 0, true)]
    [Arguments("group", false, 3, true)]
    [Arguments("group", false, int.MaxValue, false)]
    [Arguments("group", true, 3, true)]
    [Arguments("group", true, int.MaxValue, false)]
    [Arguments("pid", false, 0, true)]
    [Arguments("pid", false, int.MaxValue, false)]
    [Arguments("pid", true, int.MaxValue, false)]
    public async Task Reap_WhenAKilledLeaderIsGone_IsVerifiedOnlyOnceItsGroupIsEmpty(string kill, bool zombie, int probesWithMembers, bool verified)
    {
        var time = new ManualTimeProvider();
        var signals = new List<SentSignal>();
        var probes = 0;
        var gone = zombie ? ZombieStat(Receipt.StartTicks) : TrainingProcessStatRead.Missing;
        var real = new LinuxTrainingProcessInspector(time,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            _ => signals.Count == 0 ? Alive(Receipt.StartTicks) : gone,
            RecordingKill(signals, () => probes++ < probesWithMembers));
        // A trainer still in the host's group is signalled by pid alone.
        var receipt = kill == "pid"
            ? Receipt with
            {
                Pgid = KillPathInspector.HostGroup
            }
            : Receipt;
        var inspector = new KillPathInspector(LiveFacts with
        {
            Pgid = receipt.Pgid
        }, real);

        var reap = await DriveAsync(ReapAsync(inspector, Serialize(receipt)), time);

        AssertEx.Equal(verified ? TrainingReapOutcome.Verified : TrainingReapOutcome.NotVerified, reap.Outcome);
        AssertEx.Equal(new SentSignal
        {
            Target = kill == "pid" ? Receipt.Pid : -Receipt.Pid,
            Signal = 15
        }, signals.Single(), "Only the SIGTERM went out; a gone leader is never signalled again.");
        AssertEx.True(probes > 0, "The group named by the gone leader's pid was probed.");
        AssertEx.True(reap.Cleared, "The row path keeps clearing as before.");
    }

    [Test]
    public async Task Reap_WhenTheKillIsDeferred_IsNotVerifiedAndKeepsTheReceipt()
    {
        var inspector = new FakeTrainingProcessInspector(LiveFacts)
        {
            PresenceAfterKill = TrainingProcessPresence.Deferred
        };

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(Receipt.RunToken, inspector.SignalledRunTokens.Single(), "The kill re-verifies the recorded run token.");
        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome);
        AssertEx.False(reap.Cleared, "An operational failure learned nothing about the process; the receipt is all that identifies it.");
    }

    /// <summary>Where the group flag is refused, a successful probe is followed by the numeric kill; a refused one learns nothing, so the receipt stays.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Reap_WhenTheProbeSucceedsButTheNumericGroupKillIsRefused_KeepsTheReceipt()
    {
        const int eperm = 1;
        var time = new ManualTimeProvider();
        var calls = new FakePidFdCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : LinuxPidFdSendStatus.Sent,
            KillErrno = eperm,
            ReadRunToken = static _ => Receipt.RunToken
        };
        var real = new LinuxTrainingProcessInspector(time,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => Alive(Receipt.StartTicks),
            calls.Kill,
            calls.Ops);

        var reap = await DriveAsync(ReapAsync(new KillPathInspector(LiveFacts, real), Serialize(Receipt)), time);

        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome);
        AssertEx.False(reap.Cleared, "A refused SIGTERM left the trainer running; the receipt is all that identifies it.");
        AssertEx.Equal($"open {Receipt.Pid},send 15 group,send 0,kill {-Receipt.Pid} 15", string.Join(',', calls.Calls));
    }

    /// <summary>The receipt matched, but the stat read after the pidfd open failed (EMFILE: the fd took the last descriptor): nothing is sent and the receipt stays.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Reap_WhenTheStatIsUnreadableAfterThePidFdOpen_SendsNothingAndKeepsTheReceipt()
    {
        var calls = new FakePidFdCalls
        {
            ReadRunToken = static _ => Receipt.RunToken
        };
        var real = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => TrainingProcessStatRead.Unreadable,
            calls.Kill,
            calls.Ops);

        var reap = await ReapAsync(new KillPathInspector(LiveFacts, real), Serialize(Receipt));

        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome);
        AssertEx.False(reap.Cleared, "A failed read learned nothing; the receipt is all that identifies the trainer.");
        AssertEx.Equal($"open {Receipt.Pid}", string.Join(',', calls.Calls));
    }

    /// <summary>Off Linux nothing can be read or probed, so a receipt follows today's policy: its pid reads gone and the group probe answers empty.</summary>
    [Test]
    [ExcludeOn(OS.Linux)]
    public async Task Reap_OffLinux_KeepsTodaysPolicy_WithoutThrowing()
    {
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System, NullLogger<LinuxTrainingProcessInspector>.Instance);

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.False(inspector.ProcessGroupHasMembers(Receipt.Pid));
        AssertEx.Equal(TrainingReapOutcome.Verified, reap.Outcome);
        AssertEx.True(reap.Cleared);
    }

    [Test]
    public async Task Reap_WhenTheInspectionThrows_IsNotVerifiedAndKeepsTheReceipt()
    {
        var inspector = Substitute.For<ITrainingProcessInspector>();
        _ = inspector.Inspect(Receipt.Pid).Returns(static _ => throw new IOException("/proc went away mid-read."));

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(TrainingReapOutcome.NotVerified, reap.Outcome);
        AssertEx.False(reap.Cleared, "A receipt whose reap failed is the only trace of a possibly live process.");
    }

    private static async Task<ReapResult> ReapAsync(ITrainingProcessInspector inspector, ReadOnlyMemory<byte>? receiptJson)
    {
        var cleared = false;
        var outcome = await TrainingProcessReaper.ReapOneAsync(inspector,
            Guid.NewGuid(),
            receiptJson,
            _ =>
            {
                cleared = true;
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            CancellationToken.None);
        return new ReapResult
        {
            Outcome = outcome,
            Cleared = cleared
        };
    }

    private static ReadOnlyMemory<byte> Serialize(TrainingLaunchReceiptV1 receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(receipt, TrainingJson.Options);

    /// <summary>Scripted <c>/proc</c> facts in front of the real inspector's kill path and group probe, whose stat and <c>kill(2)</c> the test scripts.</summary>
    private sealed class KillPathInspector : ITrainingProcessInspector
    {
        public const int HostGroup = 777;

        private readonly TrainingProcessFacts _facts;
        private readonly LinuxTrainingProcessInspector _real;

        public KillPathInspector(TrainingProcessFacts facts, LinuxTrainingProcessInspector real)
        {
            _facts = facts;
            _real = real;
        }

        public int HostProcessGroupId => HostGroup;

        public TrainingProcessFacts? Inspect(int processId) =>
            _facts;

        public TrainingProcessPresence ProbePresence(int processId) =>
            TrainingProcessPresence.Alive;

        public bool ProcessGroupHasMembers(int processGroupId) =>
            _real.ProcessGroupHasMembers(processGroupId);

        public Task<TrainingProcessPresence> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, string expectedRunToken, CancellationToken cancellationToken = default) =>
            _real.KillProcessGroupAsync(processGroupId, expectedStartTicks, expectedRunToken, cancellationToken);

        public Task<TrainingProcessPresence> KillProcessAsync(int processId, long expectedStartTicks, string expectedRunToken, CancellationToken cancellationToken = default) =>
            _real.KillProcessAsync(processId, expectedStartTicks, expectedRunToken, cancellationToken);
    }

    private sealed class ReapResult
    {
        public required TrainingReapOutcome Outcome { get; init; }

        public required bool Cleared { get; init; }
    }
}
