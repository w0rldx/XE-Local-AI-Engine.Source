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
    public void For_KeepsTheWordsOfAScriptArgumentAndAPathArgument_ButStillRedactsAWholeArgumentToken()
    {
        // Windows tester round 3: arguments were split on whitespace, so "[canary]" and "STEP1canary" echoed by a cmd /c script, and the
        // DLL path hostfxr named, came back as [REDACTED]. An argument is redacted only as a whole token; a fully qualified path never.
        var path = OperatingSystem.IsWindows() ? @"C:\probe\probe-mcp.dll" : "/opt/probe/probe-mcp.dll";
        var tail = SandboxStderrTail.For(new SandboxCommandRequest
        {
            ExecutionId = "run",
            Executable = "cmd.exe",
            Arguments = ["/c", "echo [canary] & echo STEP1canary", path, "arg-secret-0123456789"]
        });

        tail.Append($"[canary] STEP1canary; The application '{path}' does not exist; key arg-secret-0123456789");

        AssertEx.Equal($"[canary] STEP1canary; The application '{path}' does not exist; key [REDACTED]", tail.Snapshot());
    }

    [Test]
    public void For_StillRedactsACredentialLikeWordInsideAScriptOrHeaderArgument()
    {
        // Security review, tester round 3: whole-token redaction alone let a token inside one multi-word argument reach the model when
        // the server echoed it alone. A 16+ letter-and-digit part or a Bearer/Basic/Token value is redacted; "powershell" is not.
        var tail = SandboxStderrTail.For(new SandboxCommandRequest
        {
            ExecutionId = "run",
            Executable = "cmd.exe",
            Arguments = ["/c", "powershell -NoProfile srv --token sk-live-abcdef1234567890", "Authorization: Bearer hdr-9f8e7d6c5b4a3210", "Authorization: Bearer letters-only-token"]
        });

        tail.Append("powershell -NoProfile: invalid value 'sk-live-abcdef1234567890' for '--token'; headers hdr-9f8e7d6c5b4a3210 letters-only-token rejected");

        AssertEx.Equal("powershell -NoProfile: invalid value '[REDACTED]' for '--token'; headers [REDACTED] [REDACTED] rejected", tail.Snapshot());
    }

    [Test]
    public void For_RedactsTheValueOfAFlagInsideAScriptArgument_EvenWithoutDigits()
    {
        // Codex review, tester round 3: "srv --token letters-only-secret" has no digits and follows no auth scheme, so the
        // letter-and-digit rule missed it. A flag's value (space or '=' separated) is a credential whatever it looks like.
        var tail = SandboxStderrTail.For(new SandboxCommandRequest
        {
            ExecutionId = "run",
            Executable = "cmd.exe",
            Arguments = ["/c", "srv --token letters-only-secret --key=assigned-letters-value ordinary-words -NoProfile"]
        });

        tail.Append("letters-only-secret assigned-letters-value ordinary-words -NoProfile srv");

        AssertEx.Equal("[REDACTED] [REDACTED] ordinary-words -NoProfile srv", tail.Snapshot());
    }

    [Test]
    public void For_RedactsAValueAssignedInsideAScriptArgument_EvenWithoutAFlag()
    {
        // Codex review (second pass), tester round 3: "set API_KEY=letters-only-secret & server.exe" has no flag and no digits, so the
        // flag rules missed the assigned value the old whitespace split had redacted.
        var tail = SandboxStderrTail.For(new SandboxCommandRequest
        {
            ExecutionId = "run",
            Executable = "cmd.exe",
            Arguments = ["/c", "set API_KEY=letters-only-secret & server.exe --verbose"]
        });

        tail.Append("API_KEY is letters-only-secret; server.exe --verbose started");

        AssertEx.Equal("API_KEY is [REDACTED]; server.exe --verbose started", tail.Snapshot());
    }

    [Test]
    public void For_RedactsACustomHeaderValueInsideAnArgument_ButDoesNotTreatAUrlAsAHeader()
    {
        // Codex review (third pass), tester round 3: "X-Api-Key: letters-only-secret" follows no flag, no scheme and has no digits.
        // A header value is a credential; a URL argument is redacted whole as every argument is, but "://" yields no "//host" secret.
        var tail = SandboxStderrTail.For(new SandboxCommandRequest
        {
            ExecutionId = "run",
            Executable = "mcp-remote",
            Arguments = ["https://example.test/mcp", "--header", "X-Api-Key: letters-only-secret", "--header", "X-Other:compact-secret-value"]
        });

        tail.Append("letters-only-secret compact-secret-value https://example.test/mcp //example.test refused");

        AssertEx.Equal("[REDACTED] [REDACTED] [REDACTED] //example.test refused", tail.Snapshot());
    }

    [Test]
    public void For_DoesNotTreatADriveLetterPathInsideAScriptAsAHeaderValue()
    {
        // Codex review (fourth pass), tester round 3: the drive colon of "C:\probe\notes.txt" matched the header separator, so the
        // rest of the path became a secret and diagnostics showed "C:[REDACTED]". Checked here on every OS by spelling the path out.
        var tail = SandboxStderrTail.For(new SandboxCommandRequest
        {
            ExecutionId = "run",
            Executable = "cmd.exe",
            Arguments = ["/c", @"type C:\probe\notes.txt & set KEY=letters-only-secret"]
        });

        tail.Append(@"C:\probe\notes.txt read; KEY letters-only-secret rejected");

        AssertEx.Equal(@"C:\probe\notes.txt read; KEY [REDACTED] rejected", tail.Snapshot());
    }

    [Test]
    public void Snapshot_WhenNothingWasWritten_IsNull()
    {
        var tail = new SandboxStderrTail(secretValues: null);
        tail.Append("   ");

        AssertEx.Null(tail.Snapshot());
    }
}
