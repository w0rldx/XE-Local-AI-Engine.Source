namespace XE_Local_AI_Engine.Tests.Training.Runs;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

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

    [Test]
    public async Task Reap_WhenTheProcessIsGone_IsVerifiedWithoutSignalling()
    {
        var inspector = new FakeTrainingProcessInspector(facts: null);

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(TrainingReapOutcome.Verified, reap.Outcome);
        AssertEx.True(reap.Cleared);
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Empty(inspector.SignalledProcesses);
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

    [Test]
    public async Task Reap_WhenThePidNowBelongsToAnotherProcess_IsVerifiedWithoutSignalling()
    {
        var inspector = new FakeTrainingProcessInspector(LiveFacts with
        {
            StartTicks = Receipt.StartTicks + 1
        });

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(TrainingReapOutcome.Verified, reap.Outcome);
        AssertEx.Empty(inspector.SignalledGroups);
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

    [Test]
    public async Task Reap_WhenAReadFieldContradictsTheReceipt_IsVerifiedEvenWithAnotherFieldUnreadable()
    {
        var inspector = new FakeTrainingProcessInspector(LiveFacts with
        {
            ExecutablePath = null,
            RunToken = "someone-else"
        });

        var reap = await ReapAsync(inspector, Serialize(Receipt));

        AssertEx.Equal(TrainingReapOutcome.Verified, reap.Outcome, "A token read and found different is a recycled pid.");
        AssertEx.Empty(inspector.SignalledGroups);
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

    private sealed class ReapResult
    {
        public required TrainingReapOutcome Outcome { get; init; }

        public required bool Cleared { get; init; }
    }
}
