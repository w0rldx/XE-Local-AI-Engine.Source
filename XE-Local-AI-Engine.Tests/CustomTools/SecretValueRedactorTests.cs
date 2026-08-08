namespace XE_Local_AI_Engine.Tests.CustomTools;

using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Value-based redaction: a known secret value is masked wherever it appears, and URL userinfo is stripped.</summary>
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
