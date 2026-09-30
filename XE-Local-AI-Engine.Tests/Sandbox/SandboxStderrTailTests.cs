namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class SandboxStderrTailTests
{
    [Test]
    public void Snapshot_KeepsOnlyTheLastLines_WithinTheCharacterBound()
    {
        var tail = new SandboxStderrTail(secretValues: null);
        for (var index = 0; index < 50; index++)
        {
            tail.Append($"line {index}");
        }

        tail.Append(new string('x', SandboxStderrTail.MaxCharacters * 2));

        var lines = tail.Snapshot()!.Split('\n');
        AssertEx.Equal(expected: 1, lines.Length);
        AssertEx.Equal(SandboxStderrTail.MaxCharacters, lines[0].Length);

        var small = new SandboxStderrTail(secretValues: null);
        for (var index = 0; index < 50; index++)
        {
            small.Append($"line {index}");
        }

        var kept = small.Snapshot()!.Split('\n');
        AssertEx.Equal(SandboxStderrTail.MaxLines, kept.Length);
        AssertEx.Equal("line 49", kept[^1]);
    }

    [Test]
    public void Append_RedactsConfiguredEnvironmentValues_ButNotShortOnes()
    {
        var tail = new SandboxStderrTail(["sk-live-0123456789", "1"]);

        tail.Append("auth failed for token sk-live-0123456789 (attempt 1)");

        AssertEx.Equal("auth failed for token [REDACTED] (attempt 1)", tail.Snapshot());
    }

    [Test]
    public void For_RedactsArgumentsAndTheBareTokenOfABearerValue_ButNeverThePath()
    {
        // Review 2026-09-30: only env values were redacted, a "Bearer <token>" value never matched the token printed alone,
        // and redacting PATH would blank the jail search path a missing-command hint quotes.
        var tail = SandboxStderrTail.For(new SandboxCommandRequest
        {
            ExecutionId = "run",
            Executable = "server",
            Arguments = ["--api-key", "arg-secret-0123456789"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AUTH"] = "Bearer tok-abcdef0123456789",
                ["PATH"] = "/opt/server/bin:/usr/bin"
            }
        });

        tail.Append("key arg-secret-0123456789 rejected; token tok-abcdef0123456789; PATH=/opt/server/bin:/usr/bin");

        AssertEx.Equal("key [REDACTED] rejected; token [REDACTED]; PATH=/opt/server/bin:/usr/bin", tail.Snapshot());
    }

    [Test]
    public void Snapshot_WhenNothingWasWritten_IsNull()
    {
        var tail = new SandboxStderrTail(secretValues: null);
        tail.Append("   ");

        AssertEx.Null(tail.Snapshot());
    }
}
