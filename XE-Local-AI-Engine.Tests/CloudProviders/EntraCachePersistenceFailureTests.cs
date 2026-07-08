namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Azure.Identity;
using Microsoft.Identity.Client.Extensions.Msal;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the InnerException chain-walk that lets the device-code / interactive-browser / authorization-code
///     no-persistence-retry fallbacks catch a persistence failure regardless of how deeply Azure.Identity or MSAL.NET
///     wrapped it (live-confirmed on WSL2: <c>AuthenticationFailedException</c> wrapping
///     <see cref="MsalCachePersistenceException" />, not the bare <see cref="CredentialUnavailableException" /> the
///     old single-type catches only handled).
/// </summary>
public sealed class EntraCachePersistenceFailureTests
{
    [Test]
    public void IsPersistenceUnavailable_WhenExceptionIsDirectlyMsalCachePersistenceException_ReturnsTrue()
    {
        AssertEx.True(EntraCachePersistenceFailure.IsPersistenceUnavailable(new MsalCachePersistenceException("boom")));
    }

    [Test]
    public void IsPersistenceUnavailable_WhenWrappedOneLevelDeep_ReturnsTrue()
    {
        var wrapped = new AuthenticationFailedException("wrapped", new MsalCachePersistenceException("boom"));

        AssertEx.True(EntraCachePersistenceFailure.IsPersistenceUnavailable(wrapped));
    }

    [Test]
    public void IsPersistenceUnavailable_WhenWrappedSeveralLevelsDeep_ReturnsTrue()
    {
        // Mirrors the live-confirmed WSL2 shape: AuthenticationFailedException -> ... -> MsalCachePersistenceException.
        var deepest = new MsalCachePersistenceException("boom");
        var middle = new InvalidOperationException("middle", deepest);
        var outer = new AuthenticationFailedException("outer", middle);

        AssertEx.True(EntraCachePersistenceFailure.IsPersistenceUnavailable(outer));
    }

    [Test]
    public void IsPersistenceUnavailable_WhenNoMsalExceptionAnywhereInChain_ReturnsFalse()
    {
        var exception = new AuthenticationFailedException("unrelated failure", new InvalidOperationException("boom"));

        AssertEx.False(EntraCachePersistenceFailure.IsPersistenceUnavailable(exception));
    }

    [Test]
    public void IsPersistenceUnavailable_WhenCredentialUnavailableExceptionAlone_ReturnsFalse()
    {
        // This helper is MSAL-specific by design — every call site combines it with its own
        // CredentialUnavailableException check (see EntraDeviceCodeSignInCoordinator, AzureFoundryChatClientFactory,
        // EntraAuthCodeConfidentialClientFactory); it must not silently subsume that check.
        AssertEx.False(EntraCachePersistenceFailure.IsPersistenceUnavailable(new CredentialUnavailableException("persistence unavailable")));
    }

    [Test]
    public void IsPersistenceUnavailable_WhenExceptionIsNull_ReturnsFalse()
    {
        AssertEx.False(EntraCachePersistenceFailure.IsPersistenceUnavailable(null));
    }
}
