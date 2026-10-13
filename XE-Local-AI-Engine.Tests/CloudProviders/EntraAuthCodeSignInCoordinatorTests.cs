namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Web;
using Microsoft.Extensions.Logging;
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
[Category(TestCategories.Integration)]
public sealed class EntraAuthCodeSignInCoordinatorTests
{
    /// <summary>HttpListener's in-use codes: EADDRINUSE on Linux (98) and macOS (48), http.sys's 32 and 183 on Windows.</summary>
    private static readonly int[] AddressInUseErrorCodes = [98, 48, 32, 183];

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
        var store = new FakeCloudCredentialStore(config: null);
        var redeemer = new RecordingRedeemer();
        using var coordinator = CreateCoordinator(store, redeemer);

        var handle = await StartOnFreePortAsync(coordinator, store);
        await SendCallbackAsync(RedirectPort(store), code: "some-code", state: "not-the-real-state");

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == EntraAuthCodeSignInState.Failed,
            TestBudgets.Contended,
            "sign-in should fail once the callback's state does not match.");
        AssertEx.Equal(0, redeemer.CallCount);
        AssertEx.True(handle.AuthorizeUrl.Contains("code_challenge=", StringComparison.Ordinal));
    }

    [Test]
    public async Task StartAsync_WhenCallbackCarriesAnAadError_SetsStatusToFailed_WithoutRedeeming()
    {
        var store = new FakeCloudCredentialStore(config: null);
        var redeemer = new RecordingRedeemer();
        using var coordinator = CreateCoordinator(store, redeemer);

        var handle = await StartOnFreePortAsync(coordinator, store);
        var state = ExtractQueryValue(handle.AuthorizeUrl, "state");
        await SendCallbackAsync(RedirectPort(store), code: null, state: state, error: "access_denied", errorDescription: "The user declined consent.");

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == EntraAuthCodeSignInState.Failed,
            TestBudgets.Contended,
            "sign-in should fail once AAD reports an error.");
        AssertEx.Equal(0, redeemer.CallCount);
    }

    /// <summary>
    ///     The callback's <c>error</c> fields are attacker-reachable — a crafted authorize URL or a compromised
    ///     gateway supplies them — so no line terminator in one may survive into the log record.
    /// </summary>
    [Test]
    public async Task StartAsync_WhenTheAadErrorCarriesLineTerminators_LogsThemEscaped()
    {
        var store = new FakeCloudCredentialStore(config: null);
        var logger = new RecordingLogger<EntraAuthCodeSignInCoordinator>();
        using var coordinator = CreateCoordinator(store, redeemer: null, logger);

        var handle = await StartOnFreePortAsync(coordinator, store);
        var state = ExtractQueryValue(handle.AuthorizeUrl, "state");
        await SendCallbackAsync(RedirectPort(store),
            code: null,
            state: state,
            error: "access_denied\r\nforged",
            errorDescription: "declined\u2028next\u0085tail");

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == EntraAuthCodeSignInState.Failed,
            TestBudgets.Contended,
            "sign-in should fail once AAD reports an error.");

        var message = AssertEx.NotNull(logger.Entries.SingleOrDefault(entry =>
            entry.Message.Contains("was rejected", StringComparison.Ordinal))).Message;
        AssertEx.False(message.Any(static character => character is '\r' or '\n' or '\u2028' or '\u0085'),
            $"A rejected sign-in must log the AAD error with no line terminator left in it, but it logged: {message}");
        AssertEx.Contains(message, "access_denied\\u000D\\u000Aforged", StringComparison.Ordinal);
        AssertEx.Contains(message, "declined\\u2028next\\u0085tail", StringComparison.Ordinal);
    }

    [Test]
    public async Task StartAsync_WhenCallbackCarriesNoCode_SetsStatusToFailed_WithoutRedeeming()
    {
        var store = new FakeCloudCredentialStore(config: null);
        var redeemer = new RecordingRedeemer();
        using var coordinator = CreateCoordinator(store, redeemer);

        var handle = await StartOnFreePortAsync(coordinator, store);
        var state = ExtractQueryValue(handle.AuthorizeUrl, "state");
        await SendCallbackAsync(RedirectPort(store), code: null, state: state);

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == EntraAuthCodeSignInState.Failed,
            TestBudgets.Contended,
            "sign-in should fail once the callback carries no authorization code.");
        AssertEx.Equal(0, redeemer.CallCount);
    }

    [Test]
    public async Task StartAsync_WhenCalledAgainWhilePending_SupersedesAndFailsThePreviousAttempt()
    {
        // Exercises the same cancellation path a real 5-minute callback-timeout would take (see this class's
        // remarks for why the timeout itself is not waited out in a test).
        var store = new FakeCloudCredentialStore(config: null);
        using var coordinator = CreateCoordinator(store);

        await StartOnFreePortAsync(coordinator, store);
        var firstPort = RedirectPort(store);
        AssertEx.Equal(EntraAuthCodeSignInState.Pending, coordinator.GetStatus().State);

        await StartOnFreePortAsync(coordinator, store);

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
        // already bound — hold a plain TcpListener on it for the whole test to force that exact failure.
        using var blocker = new TcpListener(IPAddress.Loopback, port: 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var store = new FakeCloudCredentialStore(CreateConfig(redirectUri: RedirectUriFor(port)));
        using var coordinator = CreateCoordinator(store);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(CancellationToken.None));
        AssertEx.Equal(EntraAuthCodeSignInState.None, coordinator.GetStatus().State);

        // Proves _pendingCts was actually cleared (not left dangling) — a retry on a free port must succeed cleanly
        // and reach Pending, not silently no-op because the failed attempt still "owns" the slot.
        var handle = await StartOnFreePortAsync(coordinator, store);
        AssertEx.NotNull(handle);
        AssertEx.Equal(EntraAuthCodeSignInState.Pending, coordinator.GetStatus().State);
    }

    [Test]
    public async Task StartAsync_WhenRedemptionThrowsAnUnexpectedExceptionType_SetsStatusToFailed_InsteadOfEscaping()
    {
        // TrackCallbackAsync runs fire-and-forget — an exception type outside the specific catches (e.g. a
        // CryptographicException from the account store's protector) must still resolve to Failed via the trailing
        // catch-all, not escape as an unobserved task exception while status stays stuck at Pending forever.
        var store = new FakeCloudCredentialStore(config: null);
        var redeemer = new ThrowingRedeemer(new CryptographicException("simulated protector failure"));
        using var coordinator = CreateCoordinator(store, redeemer);

        var handle = await StartOnFreePortAsync(coordinator, store);
        var state = ExtractQueryValue(handle.AuthorizeUrl, "state");
        await SendCallbackAsync(RedirectPort(store), code: "some-code", state: state);

        await AssertEx.EventuallyAsync(() => coordinator.GetStatus().State == EntraAuthCodeSignInState.Failed,
            TestBudgets.Contended,
            "an unexpected exception type during redemption must resolve to Failed, not leave status stuck at Pending.");
    }

    [Test]
    public void GetStatus_BeforeAnyStartAsync_ReturnsNone()
    {
        using var coordinator = CreateCoordinator(new FakeCloudCredentialStore(config: null));

        AssertEx.Equal(EntraAuthCodeSignInState.None, coordinator.GetStatus().State);
    }

    private static EntraAuthCodeSignInCoordinator CreateCoordinator(ICloudCredentialStore credentialStore,
        IEntraAuthCodeRedeemer? redeemer = null,
        ILogger<EntraAuthCodeSignInCoordinator>? logger = null)
    {
        return new EntraAuthCodeSignInCoordinator(credentialStore,
            new FakeEntraAuthCodeAccountStore(),
            new EntraLiveCredentialCache(),
            redeemer ?? new RecordingRedeemer(),
            logger ?? NullLogger<EntraAuthCodeSignInCoordinator>.Instance,
            TimeProvider.System);
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

    /// <summary>
    ///     Points the stored connection at a candidate loopback port and starts a sign-in there, retrying on another
    ///     candidate only when the listener reports the port already in use (wiki 17, "Independence").
    /// </summary>
    private static Task<EntraAuthCodeSignInHandle> StartOnFreePortAsync(EntraAuthCodeSignInCoordinator coordinator, FakeCloudCredentialStore store) =>
        LoopbackPort.BindWithRetryAsync<EntraAuthCodeSignInHandle>(async port =>
        {
            store.Config = CreateConfig(redirectUri: RedirectUriFor(port));
            try
            {
                return await coordinator.StartAsync(CancellationToken.None);
            }
            catch (InvalidOperationException exception) when (exception.InnerException is HttpListenerException listenerException
                                                              && AddressInUseErrorCodes.Contains(listenerException.ErrorCode))
            {
                return null;
            }
        });

    private static string RedirectUriFor(int port) =>
        string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/signin-oidc");

    private static int RedirectPort(FakeCloudCredentialStore store) =>
        new Uri(AssertEx.NotNull(store.Config?.AzureFoundry?.EntraAuthCodeRedirectUri)).Port;

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

    private sealed class FakeCloudCredentialStore : ICloudCredentialStore
    {
        public FakeCloudCredentialStore(StoredCloudProviderConfig? config)
        {
            Config = config;
        }

        public StoredCloudProviderConfig? Config { get; set; }

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
    private sealed class ThrowingRedeemer : IEntraAuthCodeRedeemer
    {
        private readonly Exception _exceptionToThrow;

        public ThrowingRedeemer(Exception exceptionToThrow)
        {
            _exceptionToThrow = exceptionToThrow;
        }

        public Task<EntraAuthCodeRedemptionResult> RedeemAsync(StoredAzureFoundryConnection connection,
            string authorizationCode,
            string codeVerifier,
            string redirectUri,
            CancellationToken cancellationToken)
        {
            throw _exceptionToThrow;
        }
    }
}
