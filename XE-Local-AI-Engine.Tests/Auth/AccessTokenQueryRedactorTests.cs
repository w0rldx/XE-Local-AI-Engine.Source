namespace XE_Local_AI_Engine.Tests.Auth;

using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AccessTokenQueryRedactorTests
{
    [Test]
    public async Task Redact_WhenAccessTokenQueryParameterExists_RemovesTokenValue()
    {
        await Task.CompletedTask;

        var redacted = AccessTokenQueryRedactor.Redact("?transport=webSockets&access_token=secret.jwt.value&id=connection-1");

        AssertEx.Equal("?transport=webSockets&access_token=[REDACTED]&id=connection-1", redacted);
        AssertEx.False(redacted.Contains("secret.jwt.value", StringComparison.Ordinal));
    }

    [Test]
    public async Task Redact_WhenAccessTokenQueryParameterIsAbsent_PreservesQuery()
    {
        await Task.CompletedTask;

        var query = "?transport=longPolling&id=connection-1";

        AssertEx.Equal(query, AccessTokenQueryRedactor.Redact(query));
    }
}
