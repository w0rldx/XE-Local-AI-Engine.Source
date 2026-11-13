namespace XE_Local_AI_Engine.Tests.Auth;

using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class SensitiveQueryRedactorTests
{
    [Test]
    public async Task Redact_WhenAccessTokenQueryParameterExists_RemovesTokenValue()
    {
        await Task.CompletedTask;

        var redacted = SensitiveQueryRedactor.Redact("?transport=webSockets&access_token=secret.jwt.value&id=connection-1");

        AssertEx.Equal("?transport=webSockets&access_token=[REDACTED]&id=connection-1", redacted);
        AssertEx.False(redacted.Contains("secret.jwt.value", StringComparison.Ordinal));
    }

    [Test]
    [Arguments("?query=secret+words&limit=5", "?query=[REDACTED]&limit=5")]
    [Arguments("?QUERY=secret", "?QUERY=[REDACTED]")]
    [Arguments("?%71uery=secret", "?%71uery=[REDACTED]")]
    public void Redact_RemovesTheConversationSearchText(string query, string expected)
    {
        AssertEx.Equal(expected, SensitiveQueryRedactor.Redact(query), "A percent-encoded key binds as the parameter it spells, so it is redacted too.");
    }

    [Test]
    public async Task Redact_WhenAccessTokenQueryParameterIsAbsent_PreservesQuery()
    {
        await Task.CompletedTask;

        var query = "?transport=longPolling&id=connection-1";

        AssertEx.Equal(query, SensitiveQueryRedactor.Redact(query));
    }
}
