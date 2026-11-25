namespace XE_Local_AI_Engine.Tests.Auth;

using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class SensitiveQueryRedactorTests
{
    [Test]
    public async Task Redact_WhenAccessTokenAndConnectionTokenExist_RemovesBothValues()
    {
        await Task.CompletedTask;

        var redacted = SensitiveQueryRedactor.Redact("?transport=webSockets&access_token=secret.jwt.value&id=connection-1");

        AssertEx.Equal("?transport=webSockets&access_token=[REDACTED]&id=[REDACTED]", redacted);
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
    [Arguments("?id=connection-token-1", "?id=[REDACTED]")]
    [Arguments("?ID=connection-token-1&_=1700000000000", "?ID=[REDACTED]&_=1700000000000")]
    [Arguments("?%69d=connection-token-1", "?%69d=[REDACTED]")]
    public void Redact_RemovesTheHubConnectionToken(string query, string expected)
    {
        AssertEx.Equal(expected, SensitiveQueryRedactor.Redact(query), "Every hub transport request carries the connection token in id; holding it drives the connection.");
    }

    [Test]
    public async Task Redact_WhenNoSensitiveQueryParameterExists_PreservesQuery()
    {
        await Task.CompletedTask;

        var query = "?transport=longPolling&agentId=agent-1&limit=5";

        AssertEx.Equal(query, SensitiveQueryRedactor.Redact(query));
    }
}
