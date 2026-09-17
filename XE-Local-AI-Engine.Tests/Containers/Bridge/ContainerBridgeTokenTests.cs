namespace XE_Local_AI_Engine.Tests.Containers.Bridge;

using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The token's shape. The id half is what makes verification a keyed row read instead of a comparison against
///     every installed application's secret, so the split has to be exact and unambiguous in both directions.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ContainerBridgeTokenTests
{
    [Test]
    public void Mint_ProducesATokenThatParsesBackToItsOwnInstance()
    {
        var instanceId = Guid.NewGuid();

        var parsed = ContainerBridgeToken.TryParse(ContainerBridgeToken.Mint(instanceId), out var roundTripped, out var secret);

        AssertEx.True(parsed, "A freshly minted token must be well formed.");
        AssertEx.Equal(instanceId, roundTripped);
        AssertEx.NotNullOrEmpty(secret);
    }

    /// <summary>
    ///     Two mints for the SAME instance must differ, or the secret is a function of the id and the id travels in
    ///     the clear.
    /// </summary>
    [Test]
    public void Mint_ProducesADifferentSecretEveryTime()
    {
        var instanceId = Guid.NewGuid();

        AssertEx.NotEqual(ContainerBridgeToken.Mint(instanceId), ContainerBridgeToken.Mint(instanceId),
            "The secret half must come from the CSPRNG, never from the instance id.");
    }

    /// <summary>
    ///     The token rides an environment variable into a container, that container's own config file and an HTTP
    ///     header. Base64url is what survives all three; padding and the base64 alphabet's other two characters do not.
    /// </summary>
    [Test]
    public void Mint_ProducesATokenSafeForAnEnvironmentVariableAndAHeader()
    {
        var token = ContainerBridgeToken.Mint(Guid.NewGuid());

        AssertEx.False(token.AsSpan().ContainsAny('+', '/', '='), $"'{token}' carries a character that does not survive the path to a container.");
        AssertEx.False(token.AsSpan().ContainsAny(' ', '\n', '\r'), $"'{token}' carries whitespace, which an HTTP header would not survive.");
    }

    [Test]
    [Arguments(null, "a null token")]
    [Arguments("", "an empty token")]
    [Arguments("nodotseparatorhere", "a token with no separator")]
    [Arguments(".secret", "a token with no instance id")]
    [Arguments("not-a-guid.secret", "a token whose id half is not a guid")]
    [Arguments("00000000000000000000000000000000.", "a token with an empty secret")]
    [Arguments("00000000-0000-0000-0000-000000000000.secret", "a token whose id is not in 'N' format")]
    public void TryParse_RefusesAMalformedToken(string? token, string description)
    {
        var parsed = ContainerBridgeToken.TryParse(token, out var instanceId, out var secret);

        AssertEx.False(parsed, $"{description} must not parse.");
        AssertEx.Equal(Guid.Empty, instanceId, "A refused parse must not leave a caller holding an instance id.");
        AssertEx.Equal(string.Empty, secret, "A refused parse must not leave a caller holding a secret.");
    }

    /// <summary>
    ///     A well-formed token is not a valid one. Parsing says only that the string has the right shape; whether the
    ///     secret matches an installed instance's is the verifier's answer, not this one's.
    /// </summary>
    [Test]
    public void TryParse_AcceptsAWellFormedTokenForAnInstanceThatNeedNotExist()
    {
        var unknown = Guid.NewGuid();

        var parsed = ContainerBridgeToken.TryParse($"{unknown:N}.cGxhdXNpYmxlLXNlY3JldA", out var instanceId, out var secret);

        AssertEx.True(parsed);
        AssertEx.Equal(unknown, instanceId);
        AssertEx.Equal("cGxhdXNpYmxlLXNlY3JldA", secret);
    }
}
