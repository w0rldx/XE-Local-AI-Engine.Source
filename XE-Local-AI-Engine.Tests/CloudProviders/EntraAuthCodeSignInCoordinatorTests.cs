namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coordinator-level tests that never touch a real MSAL confidential-client redemption or a real AAD endpoint:
///     each scenario here resolves before <see cref="IEntraAuthCodeRedeemer.RedeemAsync" /> would ever be called
///     (state mismatch, an AAD error callback, a rejected non-loopback redirect URI, and superseding a pending
///     attempt — the same cancellation path a real callback timeout takes). The one path that DOES call the
///     redeemer (a successful callback) is covered instead by the pipeline-execution test in
///     <see cref="AzureFoundryV1PipelineExecutionTests" />, which exercises the resulting live-cached credential
///     through the real wire pipeline — <see cref="IEntraAuthCodeRedeemer" /> exists specifically because MSAL's own
///     fluent redemption builders are sealed/internal and not mockable (see its remarks), so faking a full
///     successful <c>AuthenticationResult</c> here would mean fabricating MSAL's own <c>IAccount</c>/<c>AccountId</c>
///     shapes for no additional coverage.
/// </summary>
public sealed class EntraAuthCodeSignInCoordinatorTests
{
    [Test]
    public async Task StartAsync_WhenNoEntraConnectionIsStored_ThrowsConnectionNotConfigured()
    {
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config: null));

        // The precondition is a typed EntraConnectionNotConfiguredException so the sign-in endpoint can surface it as a
        // 400 while letting every other InvalidOperationException from the flow fall through to the global 500 handler.
        await AssertEx.ThrowsAsync<EntraConnectionNotConfiguredException>(() => coordinator.StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task StartAsync_WhenSignInMethodIsNotAuthorizationCode_ThrowsConnectionNotConfigured()
    {
        var config = CreateConfig(signInMethod: EntraSignInMethod.DeviceCode);
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config));

        await AssertEx.ThrowsAsync<EntraConnectionNotConfiguredException>(() => coordinator.StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task StartAsync_WhenNoClientSecretIsStored_ThrowsConnectionNotConfigured()
    {
        var config = CreateConfig(clientSecret: null);
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config));

        await AssertEx.ThrowsAsync<EntraConnectionNotConfiguredException>(() => coordinator.StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task StartAsync_WhenRedirectUriIsNotLoopback_ThrowsInvalidOperationException_WithoutBindingAListener()
    {
        var config = CreateConfig(redirectUri: "http://example.com/signin-oidc");
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config));

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(CancellationToken.None));
    }

    [Test]
    public async Task StartAsync_WhenCallbackStateDoesNotMatch_SetsStatusToFailed_WithoutRedeeming()
    {
        var port = GetFreeLoopbackPort();
        var config = CreateConfig(redirectUri: $"http://127.0.0.1:{port}/signin-oidc");
        var redeemer = new RecordingRedeemer();
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config), redeemer);

        var handle = await coordinator.StartAsync(CancellationToken.None);
        await SendCallbackAsync(port, code: "some-code", state: "not-the-real-state");

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == EntraAuthCodeSignInState.Failed,
            TimeSpan.FromSeconds(5),
            "sign-in should fail once the callback's state does not match.");
        AssertEx.Equal(0, redeemer.CallCount);
        AssertEx.True(handle.AuthorizeUrl.Contains("code_challenge=", StringComparison.Ordinal));
    }

    [Test]
    public async Task StartAsync_WhenCallbackCarriesAnAadError_SetsStatusToFailed_WithoutRedeeming()
    {
        var port = GetFreeLoopbackPort();
        var config = CreateConfig(redirectUri: $"http://127.0.0.1:{port}/signin-oidc");
        var redeemer = new RecordingRedeemer();
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config), redeemer);

        var handle = await coordinator.StartAsync(CancellationToken.None);
        var state = ExtractQueryValue(handle.AuthorizeUrl, "state");
        await SendCallbackAsync(port, code: null, state: state, error: "access_denied", errorDescription: "The user declined consent.");

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == EntraAuthCodeSignInState.Failed,
            TimeSpan.FromSeconds(5),
            "sign-in should fail once AAD reports an error.");
        AssertEx.Equal(0, redeemer.CallCount);
    }

    [Test]
    public async Task StartAsync_WhenCallbackCarriesNoCode_SetsStatusToFailed_WithoutRedeeming()
    {
        var port = GetFreeLoopbackPort();
        var config = CreateConfig(redirectUri: $"http://127.0.0.1:{port}/signin-oidc");
        var redeemer = new RecordingRedeemer();
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config), redeemer);

        var handle = await coordinator.StartAsync(CancellationToken.None);
        var state = ExtractQueryValue(handle.AuthorizeUrl, "state");
        await SendCallbackAsync(port, code: null, state: state);

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == EntraAuthCodeSignInState.Failed,
            TimeSpan.FromSeconds(5),
            "sign-in should fail once the callback carries no authorization code.");
        AssertEx.Equal(0, redeemer.CallCount);
    }

    [Test]
    public async Task StartAsync_WhenCalledAgainWhilePending_SupersedesAndFailsThePreviousAttempt()
    {
        // Exercises the same cancellation path a real 5-minute callback-timeout would take (see this class's
        // remarks for why the timeout itself is not waited out in a test).
        var firstPort = GetFreeLoopbackPort();
        var config = CreateConfig(redirectUri: $"http://127.0.0.1:{firstPort}/signin-oidc");
        var store = new FakeCloudCredentialStore(config);
        using var coordinator = CreateCoordinator(store);

        await coordinator.StartAsync(CancellationToken.None);
        AssertEx.Equal(EntraAuthCodeSignInState.Pending, coordinator.GetStatus().State);

        var secondPort = GetFreeLoopbackPort();
        store.Config = CreateConfig(redirectUri: $"http://127.0.0.1:{secondPort}/signin-oidc");
        await coordinator.StartAsync(CancellationToken.None);

        AssertEx.Equal(EntraAuthCodeSignInState.Pending, coordinator.GetStatus().State);

        // Proves the FIRST attempt's loopback listener was actually stopped (not leaked) — otherwise this bind
        // would fail with "address already in use".
        using var probe = new TcpListener(IPAddress.Loopback, firstPort);
        probe.Start();
        probe.Stop();
    }

    [Test]
    public async Task StartAsync_WhenTheRedirectPortIsAlreadyBound_ThrowsInvalidOperationException_AndCleansUpPendingState()
    {
        // HttpListener.Start() throws HttpListenerException (not InvalidOperationException) when the port is
        // already bound — occupy it first with a plain TcpListener to force that exact failure.
        var port = GetFreeLoopbackPort();
        var config = CreateConfig(redirectUri: $"http://127.0.0.1:{port}/signin-oidc");
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config));
        using var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(CancellationToken.None));
        AssertEx.Equal(EntraAuthCodeSignInState.None, coordinator.GetStatus().State);

        blocker.Stop();

        // Proves _pendingCts was actually cleared (not left dangling) — a retry against the now-free port must
        // succeed cleanly and reach Pending, not silently no-op because the failed attempt still "owns" the slot.
        var handle = await coordinator.StartAsync(CancellationToken.None);
        AssertEx.NotNull(handle);
        AssertEx.Equal(EntraAuthCodeSignInState.Pending, coordinator.GetStatus().State);
    }

    [Test]
    public async Task StartAsync_WhenRedemptionThrowsAnUnexpectedExceptionType_SetsStatusToFailed_InsteadOfEscaping()
    {
        // TrackCallbackAsync runs fire-and-forget — an exception type outside the specific catches (e.g. a
        // CryptographicException from the account store's protector) must still resolve to Failed via the trailing
        // catch-all, not escape as an unobserved task exception while status stays stuck at Pending forever.
        var port = GetFreeLoopbackPort();
        var config = CreateConfig(redirectUri: $"http://127.0.0.1:{port}/signin-oidc");
        var redeemer = new ThrowingRedeemer(new CryptographicException("simulated protector failure"));
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config), redeemer);

        var handle = await coordinator.StartAsync(CancellationToken.None);
        var state = ExtractQueryValue(handle.AuthorizeUrl, "state");
        await SendCallbackAsync(port, code: "some-code", state: state);

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == EntraAuthCodeSignInState.Failed,
            TimeSpan.FromSeconds(5),
            "an unexpected exception type during redemption must resolve to Failed, not leave status stuck at Pending.");
    }

    [Test]
    public void GetStatus_BeforeAnyStartAsync_ReturnsNone()
    {
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config: null));

        AssertEx.Equal(EntraAuthCodeSignInState.None, coordinator.GetStatus().State);
    }

    private static EntraAuthCodeSignInCoordinator CreateCoordinator(ICloudCredentialStore credentialStore, IEntraAuthCodeRedeemer? redeemer = null)
    {
        return new EntraAuthCodeSignInCoordinator(credentialStore,
            new FakeEntraAuthCodeAccountStore(),
            new EntraLiveCredentialCache(),
            redeemer ?? new RecordingRedeemer(),
            NullLogger<EntraAuthCodeSignInCoordinator>.Instance);
    }

    private static StoredCloudProviderConfig CreateConfig(string? clientSecret = "client-secret",
        EntraSignInMethod signInMethod = EntraSignInMethod.AuthorizationCode,
        string? redirectUri = null)
    {
        return new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = "https://example.openai.azure.com/",
                AuthMode = AzureFoundryAuthMode.EntraId,
                EntraTenantId = "tenant-id",
                EntraClientId = "client-id",
                EntraClientSecret = clientSecret,
                EntraTokenScope = "api://backend-app/access_as_user",
                EntraSignInMethod = signInMethod,
                EntraAuthCodeRedirectUri = redirectUri,
                Models =
                [
                    new StoredAzureFoundryModel
                    {
                        DeploymentName = "gpt-4o"
                    }
                ]
            }
        };
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task SendCallbackAsync(int port, string? code, string? state, string? error = null, string? errorDescription = null)
    {
        using var httpClient = new HttpClient();
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (code is not null)
        {
            query["code"] = code;
        }

        if (state is not null)
        {
            query["state"] = state;
        }

        if (error is not null)
        {
            query["error"] = error;
        }

        if (errorDescription is not null)
        {
            query["error_description"] = errorDescription;
        }

        await httpClient.GetAsync(new Uri($"http://127.0.0.1:{port}/signin-oidc?{query}"));
    }

    private static string ExtractQueryValue(string url, string key)
    {
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);
        return AssertEx.NotNull(query[key], $"expected query parameter '{key}' on {url}");
    }

    private sealed class FakeCloudCredentialStore(StoredCloudProviderConfig? config) : ICloudCredentialStore
    {
        public StoredCloudProviderConfig? Config { get; set; } = config;

        public Task<StoredCloudProviderConfig?> LoadConfigAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Config);
        }

        public Task SaveConfigAsync(StoredCloudProviderConfig config, CancellationToken cancellationToken = default)
        {
            Config = config;
            return Task.CompletedTask;
        }

        public Task<StoredCloudCredentials?> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<StoredCloudCredentials?>(null);
        }

        public Task SaveAsync(StoredCloudCredentials credentials, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Config = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEntraAuthCodeAccountStore : IEntraAuthCodeAccountStore
    {
        public string? SavedHomeAccountId { get; private set; }

        public Task<string?> LoadHomeAccountIdAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SavedHomeAccountId);
        }

        public Task SaveHomeAccountIdAsync(string homeAccountId, CancellationToken cancellationToken = default)
        {
            SavedHomeAccountId = homeAccountId;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            SavedHomeAccountId = null;
            return Task.CompletedTask;
        }
    }

    // Never expected to be called in these tests — every scenario resolves before redemption (see class remarks).
    // Throws if a test's assumptions were wrong instead of silently returning a fabricated MSAL result.
    private sealed class RecordingRedeemer : IEntraAuthCodeRedeemer
    {
        public int CallCount { get; private set; }

        public Task<EntraAuthCodeRedemptionResult> RedeemAsync(StoredAzureFoundryConnection connection,
            string authorizationCode,
            string codeVerifier,
            string redirectUri,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("RedeemAsync should not have been called in this test.");
        }
    }

    // Simulates an unexpected exception type escaping redemption — distinct from RecordingRedeemer, whose
    // exception means "this test's assumptions were wrong," not "simulate a real failure mode."
    private sealed class ThrowingRedeemer(Exception exceptionToThrow) : IEntraAuthCodeRedeemer
    {
        public Task<EntraAuthCodeRedemptionResult> RedeemAsync(StoredAzureFoundryConnection connection,
            string authorizationCode,
            string codeVerifier,
            string redirectUri,
            CancellationToken cancellationToken)
        {
            throw exceptionToThrow;
        }
    }
}
