namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Vault;
using XE_Local_AI_Engine.Client.Services.Vault.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The vault passphrase IS the admin password: setup, change and reset move the <c>node.key</c> wrap and the
///     Identity hash together, and a refused step leaves <c>node.key</c> byte-identical.
/// </summary>
/// <remarks>
///     Runs the real <c>NodeAuthService</c> on a host that manages the vault (<see cref="NodeVault.ManagedConfigurationKey" />),
///     with the fixture's env secret standing in for the generated desktop key.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class NodeAuthVaultTests
{
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";
    private const string NewPassword = "R3placed!Password456";

    // TestServerWebAppFactory's XE_NODE_SQLITE_KEY: bytes 1..32.
    private static readonly byte[] FixtureSecret = Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray();

    [Test]
    public async Task Setup_WhenManaged_ReturnsARecoveryCodeAndWrapsTheSecret()
    {
        await using var factory = CreateManagedFactory();
        using var client = factory.CreateClient();
        AssertEx.Equal(NodeAuthVaultStatus.Pending, await GetVaultStatusAsync(client));

        var recoveryCode = await SetupAsync(client);

        var file = VaultFileCodec.Read(await File.ReadAllBytesAsync(KeyPath(factory)));
        AssertSecret(VaultFileCodec.UnwrapWithPassword(file, Password));
        AssertSecret(VaultFileCodec.UnwrapWithRecovery(file, recoveryCode));
        AssertEx.Equal(NodeAuthVaultStatus.Unlocked, await GetVaultStatusAsync(client));
    }

    [Test]
    public async Task Setup_UnderAnExternalSecret_ReturnsNoRecoveryCodeAndReportsUnlocked()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        AssertEx.Equal(NodeAuthVaultStatus.Unlocked, await GetVaultStatusAsync(client));

        using var response = await PostSetupAsync(client);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Null(AssertEx.NotNull(await response.Content.ReadFromJsonAsync<NodeSetupResponse>()).RecoveryCode);
        AssertEx.False(File.Exists(KeyPath(factory)), "An operator-custody secret must never be written as node.key.");
    }

    [Test]
    public async Task ChangePassword_WithAWrongCurrentPassword_LeavesTheVaultByteIdentical()
    {
        await using var factory = CreateManagedFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client);
        var before = await File.ReadAllBytesAsync(KeyPath(factory));
        var token = await LoginAsync(client, Password);

        using var response = await ChangeAsync(client, token, "Wr0ng!Password999", NewPassword);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertVaultUnchangedAsync(factory, before);
    }

    [Test]
    public async Task ChangePassword_WithTheCurrentPassword_RewrapsSoOnlyTheNewPasswordUnlocks()
    {
        await using var factory = CreateManagedFactory();
        using var client = factory.CreateClient();
        var recoveryCode = await SetupAsync(client);
        var token = await LoginAsync(client, Password);

        using var response = await ChangeAsync(client, token, Password, NewPassword);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var file = VaultFileCodec.Read(await File.ReadAllBytesAsync(KeyPath(factory)));
        AssertSecret(VaultFileCodec.UnwrapWithPassword(file, NewPassword));
        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithPassword(file, Password));
        AssertSecret(VaultFileCodec.UnwrapWithRecovery(file, recoveryCode));
    }

    [Test]
    public async Task ChangePassword_WhenIdentityRejectsTheNewPassword_RestoresTheVault()
    {
        await using var factory = CreateManagedFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client);
        var before = await File.ReadAllBytesAsync(KeyPath(factory));
        var token = await LoginAsync(client, Password);

        // Long enough for the endpoint validator, but Identity's default policy wants a digit, an upper-case letter and a
        // symbol: the vault re-wrap succeeds first, then Identity refuses, and the vault must be put back.
        using var response = await ChangeAsync(client, token, Password, "onlylowercaseletters");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertVaultUnchangedAsync(factory, before);
        AssertSecret(VaultFileCodec.UnwrapWithPassword(VaultFileCodec.Read(before), Password));
    }

    [Test]
    public async Task ResetAdminPassword_WhenManaged_RequiresAValidRecoveryCode()
    {
        await using var factory = CreateManagedFactory();
        using var client = factory.CreateClient();
        var recoveryCode = await SetupAsync(client);
        var before = await File.ReadAllBytesAsync(KeyPath(factory));

        var withoutCode = await ResetAsync(factory, recoveryCode: null);
        var wrongCode = await ResetAsync(factory, recoveryCode: new string('A', 40));

        AssertEx.False(withoutCode.Succeeded);
        AssertEx.Contains(withoutCode.Errors.Single(), "recovery code");
        AssertEx.False(wrongCode.Succeeded);
        await AssertVaultUnchangedAsync(factory, before);

        var reset = await ResetAsync(factory, recoveryCode);

        AssertEx.True(reset.Succeeded, string.Join(" ", reset.Errors));
        AssertSecret(VaultFileCodec.UnwrapWithPassword(VaultFileCodec.Read(await File.ReadAllBytesAsync(KeyPath(factory))), NewPassword));
        _ = await LoginAsync(client, NewPassword);
    }

    [Test]
    public async Task ConfirmLegacyVault_WrapsALegacyKeyOnceAndThenAnswersConflict()
    {
        await using var factory = CreateManagedFactory();
        using var client = factory.CreateClient();
        // A pre-vault install: the raw base64 key on disk and an admin that setup created before the vault existed. A
        // missing file and a legacy file both read as Pending, so writing it after the host started changes no state.
        await File.WriteAllTextAsync(KeyPath(factory), Convert.ToBase64String(FixtureSecret));
        await CreateLegacyAdminAsync(factory);
        AssertEx.Equal(NodeAuthVaultStatus.Pending, await GetVaultStatusAsync(client));
        var token = await LoginAsync(client, Password);

        using var wrong = await ConfirmAsync(client, token, "Wr0ng!Password999");
        AssertEx.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        AssertEx.Equal(VaultFileFormat.Legacy, VaultFileCodec.Detect(await File.ReadAllBytesAsync(KeyPath(factory))));

        using var confirmed = await ConfirmAsync(client, token, Password);
        AssertEx.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var body = AssertEx.NotNull(await confirmed.Content.ReadFromJsonAsync<NodeVaultConfirmResponse>());
        var file = VaultFileCodec.Read(await File.ReadAllBytesAsync(KeyPath(factory)));
        AssertSecret(VaultFileCodec.UnwrapWithPassword(file, Password));
        AssertSecret(VaultFileCodec.UnwrapWithRecovery(file, body.RecoveryCode));
        AssertEx.Equal(NodeAuthVaultStatus.Unlocked, await GetVaultStatusAsync(client));

        using var again = await ConfirmAsync(client, token, Password);
        AssertEx.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Test]
    public async Task ConfirmLegacyVault_TwoConcurrentConfirms_ExactlyOneWrapsAndItsCodeUnlocks()
    {
        await using var factory = CreateManagedFactory();
        using var client = factory.CreateClient();
        await File.WriteAllTextAsync(KeyPath(factory), Convert.ToBase64String(FixtureSecret));
        await CreateLegacyAdminAsync(factory);
        var token = await LoginAsync(client, Password);

        var responses = await Task.WhenAll(ConfirmAsync(client, token, Password), ConfirmAsync(client, token, Password));
        try
        {
            AssertEx.Equal(1, responses.Count(static response => response.StatusCode == HttpStatusCode.OK));
            AssertEx.Equal(1, responses.Count(static response => response.StatusCode == HttpStatusCode.Conflict));
            var winner = responses.First(static response => response.StatusCode == HttpStatusCode.OK);
            var body = AssertEx.NotNull(await winner.Content.ReadFromJsonAsync<NodeVaultConfirmResponse>());
            var file = VaultFileCodec.Read(await File.ReadAllBytesAsync(KeyPath(factory)));
            AssertSecret(VaultFileCodec.UnwrapWithRecovery(file, body.RecoveryCode));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task VaultCreate_WhenAlreadyCreated_ThrowsAndLeavesTheFileByteIdentical()
    {
        await using var factory = CreateManagedFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client);
        var before = await File.ReadAllBytesAsync(KeyPath(factory));
        var vault = factory.Services.GetRequiredService<INodeVault>();
        AssertEx.Equal(VaultState.Unlocked, vault.State);

        _ = await AssertEx.ThrowsAsync<VaultAlreadyCreatedException>(() => vault.CreateAsync(NewPassword, replaceUnlocked: false, CancellationToken.None));

        await AssertVaultUnchangedAsync(factory, before);
        AssertEx.Equal(VaultState.Unlocked, vault.State);
    }

    [Test]
    public async Task Setup_AfterTheIdentityWasResetOnAnUnlockedVault_RewrapsUnderTheNewPasswordAndCode()
    {
        await using var factory = CreateManagedFactory();
        using var client = factory.CreateClient();
        var oldCode = await SetupAsync(client);
        await DeleteAdminAsync(factory);
        AssertEx.Equal(VaultState.Unlocked, factory.Services.GetRequiredService<INodeVault>().State);

        using var response = await client.PostAsJsonAsync("/api/local/v1/auth/setup",
            new
            {
                email = Email,
                password = NewPassword
            });

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var newCode = AssertEx.NotNull(AssertEx.NotNull(await response.Content.ReadFromJsonAsync<NodeSetupResponse>()).RecoveryCode);
        AssertEx.NotEqual(oldCode, newCode);
        var file = VaultFileCodec.Read(await File.ReadAllBytesAsync(KeyPath(factory)));
        AssertSecret(VaultFileCodec.UnwrapWithPassword(file, NewPassword));
        AssertSecret(VaultFileCodec.UnwrapWithRecovery(file, newCode));
        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithPassword(file, Password));
        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithRecovery(file, oldCode));
    }

    [Test]
    public async Task ConfirmLegacyVault_WithAChangePasswordArrivingMidWrap_EndsOnTheFinalPassword()
    {
        var vaultGate = new GatedCreateVault.Gate();
        await using var factory = CreateManagedFactory(services =>
        {
            services.AddSingleton<NodeVault>();
            services.AddSingleton<INodeVault>(provider => new GatedCreateVault(provider.GetRequiredService<NodeVault>(), vaultGate));
        });
        using var client = factory.CreateClient();
        await File.WriteAllTextAsync(KeyPath(factory), Convert.ToBase64String(FixtureSecret));
        await CreateLegacyAdminAsync(factory);
        var token = await LoginAsync(client, Password);

        // The confirm has verified the old password and is parked just before its wrap; the change arrives now. Unserialized,
        // the change would update Identity while the vault is still Pending and the wrap would then seal the OLD password.
        var responses = await Task.WhenAll(ConfirmAsync(client, token, Password), ChangeOnceTheWrapIsParkedAsync());
        using var confirmed = responses[0];
        using var changed = responses[1];
        AssertEx.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        AssertEx.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        var file = VaultFileCodec.Read(await File.ReadAllBytesAsync(KeyPath(factory)));
        AssertSecret(VaultFileCodec.UnwrapWithPassword(file, NewPassword));
        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithPassword(file, Password));
        _ = await LoginAsync(client, NewPassword);

        async Task<HttpResponseMessage> ChangeOnceTheWrapIsParkedAsync()
        {
            try
            {
                await vaultGate.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                // Started before the release so it is in flight while the wrap is parked; awaited below, before the client is disposed.
#pragma warning disable CA2025
                var change = ChangeAsync(client, token, Password, NewPassword);
#pragma warning restore CA2025
                vaultGate.ReleaseCreate.SetResult();
                return await change;
            }
            finally
            {
                // Never leave the confirm parked if the wait timed out.
                vaultGate.ReleaseCreate.TrySetResult();
            }
        }
    }

    [Test]
    public async Task ConfirmLegacyVault_UnderAnExternalSecret_AnswersConflict()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var setup = await PostSetupAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, setup.StatusCode);
        var token = await LoginAsync(client, Password);

        using var response = await ConfirmAsync(client, token, Password);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static TestServerWebAppFactory CreateManagedFactory(Action<IServiceCollection>? configureServices = null) =>
        new()
        {
            ConfigureAdditionalTestServices = configureServices,
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [NodeVault.ManagedConfigurationKey] = "true"
            }
        };

    private static string KeyPath(TestServerWebAppFactory factory) =>
        Path.Combine(factory.Services.GetRequiredService<INodeDataDirectory>().Root, VaultFileCodec.KeyFileName);

    private static async Task AssertVaultUnchangedAsync(TestServerWebAppFactory factory, byte[] before)
    {
        var after = await File.ReadAllBytesAsync(KeyPath(factory));
        AssertEx.True(before.AsSpan().SequenceEqual(after), "A refused password step must leave node.key byte-identical.");
    }

    private static void AssertSecret(byte[] unwrapped)
    {
        AssertEx.True(FixtureSecret.AsSpan().SequenceEqual(unwrapped), "The vault must wrap the host's operator secret unchanged.");
    }

    private static async Task CreateLegacyAdminAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>();
        var user = new NodeUser
        {
            Email = Email,
            UserName = Email,
            SetupCompleted = true
        };
        AssertEx.True((await userManager.CreateAsync(user, Password)).Succeeded);
        AssertEx.True((await userManager.AddToRoleAsync(user, NodeAuthorizationPolicies.AdminRole)).Succeeded);
    }

    private static async Task DeleteAdminAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>();
        var admin = AssertEx.NotNull(await userManager.FindByEmailAsync(Email));
        AssertEx.True((await userManager.DeleteAsync(admin)).Succeeded);
    }

    private static async Task<NodePasswordChangeResult> ResetAsync(TestServerWebAppFactory factory, string? recoveryCode)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                          .ResetAdminPasswordAsync(NewPassword, recoveryCode, CancellationToken.None);
    }

    private static async Task<string> GetVaultStatusAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<NodeAuthStatusResponse>("/api/local/v1/auth/status");
        return AssertEx.NotNull(status).Vault;
    }

    private static Task<HttpResponseMessage> PostSetupAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/local/v1/auth/setup",
            new
            {
                email = Email,
                password = Password
            });

    private static async Task<string> SetupAsync(HttpClient client)
    {
        using var response = await PostSetupAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<NodeSetupResponse>());
        return AssertEx.NotNull(body.RecoveryCode);
    }

    private static async Task<string> LoginAsync(HttpClient client, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password
            });
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return AssertEx.NotNull(await response.Content.ReadFromJsonAsync<NodeAccessTokenResponse>()).AccessToken;
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string token, string currentPassword, string newPassword) =>
        SendAuthorizedAsync(client,
            token,
            "/api/local/v1/auth/change-password",
            new
            {
                currentPassword,
                newPassword
            });

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string token, string password) =>
        SendAuthorizedAsync(client,
            token,
            "/api/local/v1/auth/vault/confirm",
            new
            {
                password
            });

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(HttpClient client, string token, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>The real vault, with <see cref="CreateAsync" /> parked on a gate the test opens.</summary>
    /// <remarks>Hand-written: a substitute would need every member forwarded to the real vault, which this does in one line each.</remarks>
    private sealed class GatedCreateVault : INodeVault
    {
        private readonly Gate _gate;
        private readonly INodeVault _inner;

        public GatedCreateVault(INodeVault inner, Gate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public VaultState State => _inner.State;

        public async Task<VaultChange?> CreateAsync(string password, bool replaceUnlocked, CancellationToken cancellationToken)
        {
            _gate.CreateEntered.TrySetResult();
            await _gate.ReleaseCreate.Task.WaitAsync(cancellationToken);
            return await _inner.CreateAsync(password, replaceUnlocked, cancellationToken);
        }

        public Task<VaultChange?> RewrapAsync(string currentPassword, string newPassword, CancellationToken cancellationToken) =>
            _inner.RewrapAsync(currentPassword, newPassword, cancellationToken);

        public Task<VaultChange?> RewrapWithRecoveryAsync(string recoveryCode, string newPassword, CancellationToken cancellationToken) =>
            _inner.RewrapWithRecoveryAsync(recoveryCode, newPassword, cancellationToken);

        public Task RestoreAsync(VaultChange change, CancellationToken cancellationToken) =>
            _inner.RestoreAsync(change, cancellationToken);

        public sealed class Gate
        {
            public TaskCompletionSource CreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource ReleaseCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
