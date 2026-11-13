namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Text;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Drives the device-code start handshake through scripted <see cref="DeviceCodeCredential" /> stand-ins, so every
///     way <c>AuthenticateAsync</c> can end before Entra hands out a code is covered without a real tenant.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class EntraDeviceCodeSignInCoordinatorTests
{
    // Failure detector only: a correct coordinator answers in milliseconds, a regressed one hangs forever.
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task StartAsync_WhenSupersededBeforeTheDeviceCodeArrives_FirstCallEndsCancelled()
    {
        var firstAuthenticating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = CreateCoordinator(
            (_, cancellationToken) =>
            {
                firstAuthenticating.TrySetResult();
                return PendingUntilCancelledAsync(cancellationToken);
            },
            async (options, cancellationToken) =>
            {
                await AssertEx.NotNull(options.DeviceCodeCallback)(DeviceCode("SECOND"), cancellationToken);
                return await PendingUntilCancelledAsync(cancellationToken);
            });

        var first = coordinator.StartAsync(CancellationToken.None);
        await firstAuthenticating.Task.WaitAsync(HangTimeout);

        var second = await coordinator.StartAsync(CancellationToken.None).WaitAsync(HangTimeout);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => first.WaitAsync(HangTimeout));
        AssertEx.Equal("SECOND", second.UserCode);
        AssertEx.Equal("SECOND", coordinator.GetStatus().UserCode);
    }

    [Test]
    public async Task StartAsync_WhenAuthenticationSucceedsWithoutADeviceCode_FailsInsteadOfHanging()
    {
        using var coordinator = CreateCoordinator(async (_, cancellationToken) =>
        {
            // AuthenticationRecord has no public constructor; its own Serialize() schema is the supported way to get one.
            const string json = """
                                {"username":"user@contoso.com","authority":"https://login.microsoftonline.com/tenant-id","homeAccountId":"home-account-id","tenantId":"tenant-id","clientId":"client-id","version":"1.0"}
                                """;
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return await AuthenticationRecord.DeserializeAsync(stream, cancellationToken);
        });

        await AssertEx.ThrowsAsync<AuthenticationFailedException>(() => coordinator.StartAsync(CancellationToken.None).WaitAsync(HangTimeout));
    }

    [Test]
    public async Task StartAsync_WhenPersistenceIsUnavailableBeforeTheDeviceCode_RetriesWithoutPersistence()
    {
        DeviceCodeCredentialOptions? retryOptions = null;
        using var coordinator = CreateCoordinator(
            (_, _) => Task.FromException<AuthenticationRecord>(new CredentialUnavailableException("no secret service")),
            async (options, cancellationToken) =>
            {
                retryOptions = options;
                await AssertEx.NotNull(options.DeviceCodeCallback)(DeviceCode("RETRY"), cancellationToken);
                return await PendingUntilCancelledAsync(cancellationToken);
            });

        var handle = await coordinator.StartAsync(CancellationToken.None).WaitAsync(HangTimeout);

        AssertEx.Equal("RETRY", handle.UserCode);
        AssertEx.Null(AssertEx.NotNull(retryOptions).TokenCachePersistenceOptions);
    }

    private static EntraDeviceCodeSignInCoordinator CreateCoordinator(params Func<DeviceCodeCredentialOptions, CancellationToken, Task<AuthenticationRecord>>[] attempts)
    {
        var credentialStore = Substitute.For<ICloudCredentialStore>();
        credentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = "https://example.openai.azure.com/",
                AuthMode = AzureFoundryAuthMode.EntraId,
                EntraTenantId = "tenant-id",
                EntraClientId = "client-id",
                EntraTokenScope = "api://backend-app/access_as_user"
            }
        });

        var remaining = new Queue<Func<DeviceCodeCredentialOptions, CancellationToken, Task<AuthenticationRecord>>>(attempts);
        return new EntraDeviceCodeSignInCoordinator(credentialStore,
            Substitute.For<IEntraTokenCacheStore>(),
            new EntraLiveCredentialCache(),
            NullLogger<EntraDeviceCodeSignInCoordinator>.Instance,
            options => new ScriptedDeviceCodeCredential(options, remaining.Dequeue()),
            onSignInSucceeded: null);
    }

    private static DeviceCodeInfo DeviceCode(string userCode) =>
        IdentityModelFactory.DeviceCodeInfo(userCode, "device-code", new Uri("https://microsoft.com/devicelogin"), DateTimeOffset.MaxValue, "message", "client-id", []);

    private static Task<AuthenticationRecord> PendingUntilCancelledAsync(CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<AuthenticationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        return pending.Task;
    }

    private sealed class ScriptedDeviceCodeCredential : DeviceCodeCredential
    {
        private readonly Func<DeviceCodeCredentialOptions, CancellationToken, Task<AuthenticationRecord>> _authenticate;
        private readonly DeviceCodeCredentialOptions _options;

        public ScriptedDeviceCodeCredential(DeviceCodeCredentialOptions options, Func<DeviceCodeCredentialOptions, CancellationToken, Task<AuthenticationRecord>> authenticate)
        {
            _options = options;
            _authenticate = authenticate;
        }

        public override Task<AuthenticationRecord> AuthenticateAsync(CancellationToken cancellationToken = default)
        {
            return _authenticate(_options, cancellationToken);
        }
    }
}
