namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.LiveOutput;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="LiveCommandOutput" />: the redaction, coalescing and caps between a sandbox pump and a live view. Untrusted process
///     output is the input, so each bound is driven past its edge; the clock is a <see cref="ManualTimeProvider" />.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LiveCommandOutputTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [Test]
    public void ALoneLine_IsDeliveredWhenTheFlushIntervalElapses_NotBefore()
    {
        var clock = new ManualTimeProvider();
        var batches = new List<LiveCommandOutputBatch>();
        using var output = new LiveCommandOutput(new LiveCommandOutputLimits(), workspaceRoot: null, batches.Add, clock);

        output.OnLine(SandboxOutputSource.StandardOutput, "hello");
        clock.Advance(Interval - TimeSpan.FromMilliseconds(1));
        AssertEx.Empty(batches);

        clock.Advance(TimeSpan.FromMilliseconds(1));

        var batch = batches.Single();
        AssertEx.Equal("hello", batch.Lines.Single().Text);
        AssertEx.Equal(SandboxOutputSource.StandardOutput, batch.Lines.Single().Source);
        AssertEx.Equal(expected: 0, batch.SkippedLines);
    }

    [Test]
    public void LinesWithinOneInterval_AreCoalescedIntoOneBatch_InArrivalOrder()
    {
        var clock = new ManualTimeProvider();
        var batches = new List<LiveCommandOutputBatch>();
        using var output = new LiveCommandOutput(new LiveCommandOutputLimits(), workspaceRoot: null, batches.Add, clock);

        output.OnLine(SandboxOutputSource.StandardOutput, "one");
        clock.Advance(TimeSpan.FromMilliseconds(40));
        output.OnLine(SandboxOutputSource.StandardError, "two");
        clock.Advance(TimeSpan.FromMilliseconds(40));
        output.OnLine(SandboxOutputSource.StandardOutput, "three");
        clock.Advance(TimeSpan.FromMilliseconds(20));

        var batch = batches.Single();
        AssertEx.Equal("one|two|three", string.Join('|', batch.Lines.Select(line => line.Text)));
        AssertEx.Equal(SandboxOutputSource.StandardError, batch.Lines[1].Source);
    }

    [Test]
    public void ABatchNeverExceedsItsCap_LinesThatDoNotFitBeforeTheNextFlushAreSkipped()
    {
        var clock = new ManualTimeProvider();
        var batches = new List<LiveCommandOutputBatch>();
        var limits = new LiveCommandOutputLimits
        {
            MaxLineChars = 10,
            MaxBatchChars = 40,
            MaxTailChars = 64
        };
        using var output = new LiveCommandOutput(limits, workspaceRoot: null, batches.Add, clock);

        // Ten 9-character lines cost 10 each; only four fit one 40-character batch.
        for (var index = 0; index < 10; index++)
        {
            output.OnLine(SandboxOutputSource.StandardOutput, "line-" + index.ToString("D4", CultureInfo.InvariantCulture));
        }

        clock.Advance(Interval);

        var first = batches.Single();
        AssertEx.Equal("line-0000|line-0001|line-0002|line-0003", string.Join('|', first.Lines.Select(line => line.Text)));
        AssertEx.Equal(expected: 6, first.SkippedLines);
        AssertEx.True(first.Lines.Sum(line => line.Text.Length + 1) <= limits.MaxBatchChars);

        // A full batch whose timer is not yet due, but whose interval since the last flush has passed, flushes on the next line.
        clock.Advance(TimeSpan.FromMilliseconds(50));
        for (var index = 0; index < 4; index++)
        {
            output.OnLine(SandboxOutputSource.StandardOutput, "fill-" + index.ToString("D4", CultureInfo.InvariantCulture));
        }

        clock.Advance(TimeSpan.FromMilliseconds(60));
        output.OnLine(SandboxOutputSource.StandardOutput, "over-0001");

        AssertEx.Equal(expected: 2, batches.Count);
        AssertEx.Equal("fill-0000|fill-0001|fill-0002|fill-0003", string.Join('|', batches[1].Lines.Select(line => line.Text)));
        AssertEx.Equal(expected: 6, batches[1].SkippedLines);
    }

    [Test]
    public void PastTheLiveBudget_LinesAreOnlyCounted_AndCompleteDeliversTheFinalCount()
    {
        var clock = new ManualTimeProvider();
        var batches = new List<LiveCommandOutputBatch>();
        var limits = new LiveCommandOutputLimits
        {
            MaxLineChars = 10,
            MaxBatchChars = 40,
            MaxLiveChars = 30,
            MaxTailChars = 64
        };
        using var output = new LiveCommandOutput(limits, workspaceRoot: null, batches.Add, clock);

        for (var index = 0; index < 8; index++)
        {
            output.OnLine(SandboxOutputSource.StandardOutput, "abcd" + index.ToString(CultureInfo.InvariantCulture));
            clock.Advance(Interval);
        }

        output.Complete();
        output.OnLine(SandboxOutputSource.StandardOutput, "after-complete");

        var delivered = batches.SelectMany(batch => batch.Lines).ToList();
        AssertEx.Equal("abcd0|abcd1|abcd2|abcd3|abcd4", string.Join('|', delivered.Select(line => line.Text)));
        AssertEx.True(delivered.Sum(line => line.Text.Length + 1) <= limits.MaxLiveChars);
        AssertEx.Equal(expected: 3, batches[^1].SkippedLines);
        AssertEx.Equal(expected: 3, output.SkippedLines);
    }

    [Test]
    public void TheTail_HoldsTheLastLinesIncludingSkippedOnes_WithinItsCap()
    {
        var clock = new ManualTimeProvider();
        var limits = new LiveCommandOutputLimits
        {
            MaxLineChars = 10,
            MaxBatchChars = 40,
            MaxLiveChars = 20,
            MaxTailChars = 64
        };
        using var output = new LiveCommandOutput(limits, workspaceRoot: null, static _ => { }, clock);

        for (var index = 0; index < 100; index++)
        {
            output.OnLine(index % 2 == 0 ? SandboxOutputSource.StandardOutput : SandboxOutputSource.StandardError,
                "t" + index.ToString("D3", CultureInfo.InvariantCulture));
        }

        var tail = output.GetTail();

        AssertEx.True(tail.Sum(line => line.Text.Length + 1) <= limits.MaxTailChars);
        AssertEx.Equal("t099", tail[^1].Text);
        AssertEx.Equal(SandboxOutputSource.StandardError, tail[^1].Source);
        // Each 4-character line costs 5, so 12 of them fit 64.
        AssertEx.Equal(expected: 12, tail.Count);
        AssertEx.Equal("t088", tail[0].Text);
    }

    [Test]
    [Arguments("Authorization: Bearer abc123", "Authorization[redacted]")]
    [Arguments("authorization=Basic dXNlcjpwYXNz", "authorization[redacted]")]
    [Arguments("{\"Authorization\": \"Basic dXNlcjpwYXNz\"}", "{\"Authorization[redacted]}")]
    [Arguments("{'Authorization': 'Bearer abc123'}", "{'Authorization[redacted]}")]
    [Arguments("Authorization=\"Basic dXNlcjpwYXNz\"", "Authorization[redacted]")]
    [Arguments("authorization: \"token abc123\"", "authorization[redacted]")]
    [Arguments("Authorization: \"Basic abc def\"", "Authorization[redacted]")]
    [Arguments("{\"GITHUB_TOKEN\": \"ghp_abc123\"}", "{\"GITHUB_TOKEN[redacted]}")]
    [Arguments("{\"api_key\": \"abc123\"}", "{\"api_key[redacted]}")]
    [Arguments("password=\"correct horse battery staple\" ok", "password[redacted] ok")]
    [Arguments("DB_PASSWORD='a b;c' next", "DB_PASSWORD[redacted] next")]
    [Arguments("{\"api_key\": \"abc, def\"}", "{\"api_key[redacted]}")]
    [Arguments("password=\"a\\\"b c\" tail", "password[redacted] tail")]
    [Arguments("password=\"abc def", "password[redacted]")]
    [Arguments("name=\"hello, world\"; size: 3", "name=\"hello, world\"; size: 3")]
    [Arguments("sent Bearer abc123 upstream", "sent Bearer [redacted] upstream")]
    [Arguments("bearer=abc123 next", "bearer [redacted] next")]
    [Arguments("bearer: abc123 next", "bearer [redacted] next")]
    [Arguments("bearer:abc123", "bearer [redacted]")]
    [Arguments("Bearer \"abc 123\" next", "Bearer [redacted] next")]
    [Arguments("export GITHUB_TOKEN=ghp_abc123", "export GITHUB_TOKEN[redacted]")]
    [Arguments("AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI", "AWS_SECRET_ACCESS_KEY[redacted]")]
    [Arguments("SSH_KEY: abc123", "SSH_KEY[redacted]")]
    [Arguments("KEY=abc123", "KEY[redacted]")]
    [Arguments("CREDENTIALS=abc123", "CREDENTIALS[redacted]")]
    [Arguments("TOKEN_VALUE=abc123", "TOKEN_VALUE[redacted]")]
    [Arguments("SECRET_X: abc123", "SECRET_X[redacted]")]
    [Arguments("token=abc123", "token[redacted]")]
    [Arguments("\"client_secret\": \"abc123\"", "\"client_secret[redacted]")]
    [Arguments("git clone https://user:abc123@github.com/o/r", "git clone https://[redacted]@github.com/o/r")]
    [Arguments("curl -H \"Authorization: Bearer abc123\" https://api.example", "curl -H \"Authorization[redacted]\" https://api.example")]
    [Arguments("monkey: 3 keys, author: Ada", "monkey: 3 keys, author: Ada")]
    public void ACredentialInALine_LosesItsValue(string line, string expected)
    {
        var clock = new ManualTimeProvider();
        var batches = new List<LiveCommandOutputBatch>();
        using var output = new LiveCommandOutput(new LiveCommandOutputLimits(), workspaceRoot: null, batches.Add, clock);

        output.OnLine(SandboxOutputSource.StandardOutput, line);
        clock.Advance(Interval);

        AssertEx.Equal(expected, batches.Single().Lines.Single().Text);
    }

    [Test]
    public void EveryLine_IsRedacted_InBatchesAndInTheTail()
    {
        var clock = new ManualTimeProvider();
        var batches = new List<LiveCommandOutputBatch>();
        var limits = new LiveCommandOutputLimits
        {
            MaxLineChars = 40
        };
        using var output = new LiveCommandOutput(limits, "/var/lib/xe/jails/abc123", batches.Add, clock);

        output.OnLine(SandboxOutputSource.StandardOutput, "cd /var/lib/xe/jails/abc123/src && make");
        output.OnLine(SandboxOutputSource.StandardOutput, "API_KEY=sk-live-123 password: hunter2");
        output.OnLine(SandboxOutputSource.StandardOutput, "token !Abcdefghijkl99 used");
        output.OnLine(SandboxOutputSource.StandardError, "\u001b[31mred\u001b[0m \u001b]0;title\u0007bell\u0007");
        output.OnLine(SandboxOutputSource.StandardOutput, "/var/lib/xe/jails/abc\u001b[1m123/x");
        output.OnLine(SandboxOutputSource.StandardOutput, new string('y', 100));
        clock.Advance(Interval);

        string[] expected =
        [
            "cd <workspace>/src && make",
            // The shared credential pattern keeps only the label, as Development's live sanitizer always has.
            "API_KEY[redacted] password[redacted]",
            "token [redacted] used",
            "red bell",
            "<workspace>/x",
            new string('y', 40) + " [line truncated]"
        ];
        AssertEx.Equal(string.Join('\n', expected), string.Join('\n', batches.Single().Lines.Select(line => line.Text)));
        AssertEx.Equal(string.Join('\n', expected), string.Join('\n', output.GetTail().Select(line => line.Text)));
    }

    [Test]
    public async Task ConcurrentStreams_AccountForEveryLine_AsDeliveredOrSkipped()
    {
        var clock = new ManualTimeProvider();
        var delivered = 0;
        var limits = new LiveCommandOutputLimits
        {
            MaxLineChars = 10,
            MaxBatchChars = 4096,
            MaxLiveChars = 10_000
        };
        using var output = new LiveCommandOutput(limits, workspaceRoot: null, batch => delivered += batch.Lines.Count, clock);
        const int LinesPerStream = 5_000;

        await Task.WhenAll(Pump(SandboxOutputSource.StandardOutput), Pump(SandboxOutputSource.StandardError));
        output.Complete();

        // The callback runs under the instance's lock, so the plain counter is not raced.
        AssertEx.Equal(2 * LinesPerStream, delivered + output.SkippedLines);
        AssertEx.True(delivered > 0, "the first batch's worth must be delivered");

        Task Pump(SandboxOutputSource source)
        {
            return Task.Run(() =>
            {
                for (var index = 0; index < LinesPerStream; index++)
                {
                    output.OnLine(source, "x");
                }
            });
        }
    }

    [Test]
    public void AThrowingCallback_NeverReachesThePump()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        using var output = new LiveCommandOutput(new LiveCommandOutputLimits(),
            workspaceRoot: null,
            _ =>
            {
                calls++;
                throw new InvalidOperationException("consumer failure under test");
            },
            clock);

        output.OnLine(SandboxOutputSource.StandardOutput, "one");
        clock.Advance(Interval);
        output.OnLine(SandboxOutputSource.StandardOutput, "two");
        AssertEx.DoesNotThrow(output.Complete, "Complete must swallow a consumer failure like the timer path does");

        AssertEx.Equal(expected: 2, calls);
    }

    [Test]
    public void Limits_ThatCannotHoldOneMarkedLine_AreRefused()
    {
        var limits = new LiveCommandOutputLimits
        {
            MaxLineChars = 2000,
            MaxBatchChars = 1024
        };

        _ = AssertEx.Throws<ArgumentOutOfRangeException>(() => new LiveCommandOutput(limits, workspaceRoot: null, static _ => { }, new ManualTimeProvider()).Dispose());
    }
}
