namespace XE_Local_AI_Engine.Tests.CustomTools;

using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Value-based redaction: a known secret value is masked wherever it appears, and URL userinfo is stripped.</summary>
[Category(TestCategories.Unit)]
public sealed class SecretValueRedactorTests
{
    [Test]
    public async Task Redact_ReplacesEverySecretValueOccurrence()
    {
        var redactor = new SecretValueRedactor(["sk-super-secret-token", "hunter2"]);
        var redacted = redactor.Redact("Authorization: Bearer sk-super-secret-token failed for password hunter2.");

        AssertEx.False(redacted.Contains("sk-super-secret-token", StringComparison.Ordinal), "The API token must be redacted.");
        AssertEx.False(redacted.Contains("hunter2", StringComparison.Ordinal), "The password must be redacted.");
        AssertEx.Contains(redacted, "[REDACTED]");
        await Task.CompletedTask;
    }

    [Test]
    public async Task WithParts_AddsTheValueOfAnAssignmentStyleArgument()
    {
        // Codex review 2026-09-30: "--api-key=secretvalue" registered only the whole argument, so a diagnostic echoing the
        // parsed value alone survived every scrub.
        var parts = SecretValueRedactor.WithParts(["--api-key=secretvalue", "Bearer tok-abcdef0123", "PATH=/usr/bin:/bin"], minLength: 8).ToList();

        AssertEx.Contains(parts, "secretvalue");
        AssertEx.Contains(parts, "tok-abcdef0123");
        AssertEx.Contains(parts, "/usr/bin:/bin");
        AssertEx.False(parts.Contains("--api-key="), "the empty-value prefix is not a secret");

        var redacted = new SecretValueRedactor(parts).Redact("server rejected key secretvalue");
        AssertEx.False(redacted.Contains("secretvalue", StringComparison.Ordinal), "the bare value must be redacted");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Redact_StripsUrlUserInfo()
    {
        var redactor = new SecretValueRedactor([]);
        var redacted = redactor.Redact("fetching https://alice:s3cr3t@api.example.com/data");

        AssertEx.False(redacted.Contains("alice:s3cr3t@", StringComparison.Ordinal), "URL userinfo must be stripped.");
        AssertEx.Contains(redacted, "https://api.example.com/data");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Redact_LongestSecretFirst_LeavesNoTail()
    {
        // A secret that is a prefix of a longer one must not leave the longer one's tail exposed.
        var redactor = new SecretValueRedactor(["abc", "abcdef"]);
        var redacted = redactor.Redact("value=abcdef");

        AssertEx.False(redacted.Contains("def", StringComparison.Ordinal), "The longer secret must be masked whole.");
        await Task.CompletedTask;
    }

    // ForCommand: the credential shapes that must stay redacted, one test each, then the flag names that are exempt (tester round 6).
    // The stderr tail uses a minimum secret length of 8.
    private static string RedactForCommand(string[] arguments, string text) =>
        new SecretValueRedactor(SecretValueRedactor.ForCommand([], arguments, minLength: 8)).Redact(text);

    [Test]
    public async Task ForCommand_ABarePositionalToken_IsRedacted()
    {
        AssertEx.Equal("bad key [REDACTED]", RedactForCommand(["serve", "hunter2pass"], "bad key hunter2pass"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForCommand_AnAssignment_RedactsTheValueAlone()
    {
        AssertEx.Equal("value [REDACTED] rejected", RedactForCommand(["API_KEY=assignedsecret"], "value assignedsecret rejected"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForCommand_AHeaderInsideAScriptArgument_RedactsTheHeaderValue()
    {
        AssertEx.Equal("header [REDACTED] rejected", RedactForCommand(["/c", "srv X-Api-Key: headersecret"], "header headersecret rejected"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForCommand_ASchemeToken_RedactsTheTokenAlone()
    {
        AssertEx.Equal("token [REDACTED] expired", RedactForCommand(["/c", "srv Authorization: Bearer lettersonlytoken"], "token lettersonlytoken expired"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForCommand_ALongMixedTokenInsideAScript_IsRedacted()
    {
        AssertEx.Equal("got [REDACTED]", RedactForCommand(["/c", "srv start abcdef0123456789xyz"], "got abcdef0123456789xyz"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForCommand_AFlagWithAnAssignedValue_RedactsTheValue()
    {
        AssertEx.Equal("[REDACTED] / [REDACTED]", RedactForCommand(["--token=gluedsecret"], "--token=gluedsecret / gluedsecret"));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("-pSecret123")]
    [Arguments("-pMyPassword")]
    [Arguments("-NoProfileX")]
    [Arguments("--Token1")]
    public async Task ForCommand_AFlagWithAGluedValue_IsRedactedWhole(string argument)
    {
        // A single-dash argument outside the PowerShell switch list may glue a value (mysql -pPassword); a digit marks one after "--".
        AssertEx.Equal("arg [REDACTED]", RedactForCommand([argument], $"arg {argument}"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForCommand_AValueArgumentAfterAFlag_RedactsTheValueAndKeepsTheFlag()
    {
        AssertEx.Equal("--password [REDACTED]", RedactForCommand(["--password", "Secret12345"], "--password Secret12345"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForCommand_AWindowsPathWithADriveColon_IsNotReadAsAHeader()
    {
        // Round-3 trap: "C:\tools\..." must not register the text after the drive colon as a header value.
        var values = SecretValueRedactor.ForCommand([], [@"C:\tools\server.js", "/c", @"srv C:\tools\other.js"], minLength: 8).ToList();

        AssertEx.False(values.Contains(@"\tools\server.js"), "the part after a drive colon is not a header value");
        AssertEx.False(values.Contains(@"\tools\other.js"), "the part after a drive colon inside a script is not a header value");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForCommand_AUrl_IsRedactedWholeAndNotReadAsAHeader()
    {
        var values = SecretValueRedactor.ForCommand([], ["https://hooks.example.com/abc"], minLength: 8).ToList();

        AssertEx.Contains(values, "https://hooks.example.com/abc");
        AssertEx.False(values.Contains("//hooks.example.com/abc"), "'://' is a URL, not a header separator");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("--no-warnings")]
    [Arguments("-NoProfile")]
    [Arguments("-noprofile")]
    [Arguments("-ExecutionPolicy")]
    [Arguments("-WindowStyle")]
    [Arguments("-configurationname")]
    [Arguments("--preserve-symlinks-main")]
    [Arguments("--max_old_space")]
    public async Task ForCommand_ABareFlagName_StaysVisible(string flag)
    {
        AssertEx.Equal($"unknown option {flag}", RedactForCommand([flag, "--token", "Secret12345"], $"unknown option {flag}"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForCommand_ARelativeScriptName_StaysRedactedByDesign()
    {
        // A bare file name is indistinguishable from a credential (a JWT, "key.secret", "id_rsa"), so it is not exempt.
        AssertEx.Equal(@"Cannot find module 'G:\refs\[REDACTED]'", RedactForCommand(["mcp-echo.js"], @"Cannot find module 'G:\refs\mcp-echo.js'"));
        await Task.CompletedTask;
    }
}
