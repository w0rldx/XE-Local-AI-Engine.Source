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
}
