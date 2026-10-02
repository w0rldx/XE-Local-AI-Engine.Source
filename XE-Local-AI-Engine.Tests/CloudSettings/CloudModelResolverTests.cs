namespace XE_Local_AI_Engine.Tests.CloudSettings;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The "is this a cloud model?" answer the local-model list/details/select endpoints share. Cloud-ness delegates to
///     the trust resolver and fails CLOSED; the Azure deployment match and connection reads stay best-effort ("no
///     deployment" / "no connection"), but a genuine cancellation must still propagate.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class CloudModelResolverTests
{
    [Test]
    public async Task StoredDeploymentName_MatchesCaseInsensitively()
    {
        var routing = Substitute.For<IActiveCloudChatClientFactory>();
        _ = routing.IsCloudProviderSelected("azure-gpt").Returns(true);
        var resolver = CreateResolver(WithDeployments("Azure-GPT"), routing);

        AssertEx.True(await resolver.IsAzureFoundryDeploymentAsync("azure-gpt"));
        AssertEx.True(await resolver.IsCloudModelAsync("azure-gpt"));
        AssertEx.False(await resolver.IsAzureFoundryDeploymentAsync("llama-3.gguf"));
        AssertEx.False(await resolver.IsCloudModelAsync("llama-3.gguf"));
    }

    [Test]
    public async Task CodexId_IsACloudModel_ButNotAnAzureDeployment()
    {
        var resolver = CreateResolver(WithDeployments("azure-gpt"));

        AssertEx.True(await resolver.IsCloudModelAsync("gpt-5.5"));
        AssertEx.False(await resolver.IsAzureFoundryDeploymentAsync("gpt-5.5"));
    }

    [Test]
    public async Task BlankModelName_IsNeverCloud()
    {
        var store = Substitute.For<ICloudCredentialStore>();
        var resolver = CreateResolver(store);

        AssertEx.False(await resolver.IsCloudModelAsync(modelName: null));
        AssertEx.False(await resolver.IsAzureFoundryDeploymentAsync("   "));
        await store.DidNotReceive().LoadConfigAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailingCredentialStore_DegradesToNoDeploymentAndNoConnection()
    {
        var store = Substitute.For<ICloudCredentialStore>();
        store.LoadConfigAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("decrypt failed"));
        var resolver = CreateResolver(store);

        AssertEx.False(await resolver.IsAzureFoundryDeploymentAsync("azure-gpt"));
        AssertEx.Null(await resolver.ResolveAzureFoundryConnectionAsync());
    }

    [Test]
    public async Task FailingRoutingSnapshot_FailsClosedToCloud()
    {
        // This answer feeds egress cues and gates; before the trust resolver owned it, a throwing store read "not cloud".
        var routing = Substitute.For<IActiveCloudChatClientFactory>();
        _ = routing.IsCloudProviderSelected("azure-gpt").Returns(_ => throw new InvalidOperationException("decrypt failed"));
        var resolver = CreateResolver(Substitute.For<ICloudCredentialStore>(), routing);

        AssertEx.True(await resolver.IsCloudModelAsync("azure-gpt"));
    }

    [Test]
    public async Task Cancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var store = Substitute.For<ICloudCredentialStore>();
        store.LoadConfigAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());
        var resolver = CreateResolver(store);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => resolver.IsAzureFoundryDeploymentAsync("azure-gpt", cancellation.Token));
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAzureFoundryConnectionAsync(cancellation.Token));
    }

    [Test]
    public async Task ResolveAzureFoundryConnection_ReturnsTheStoredConnection()
    {
        var resolver = CreateResolver(WithDeployments("azure-gpt", "azure-mini"));

        var connection = await resolver.ResolveAzureFoundryConnectionAsync();

        AssertEx.Equal(expected: 2, connection!.Models.Count);
    }

    private static ICloudCredentialStore WithDeployments(params string[] deploymentNames)
    {
        var store = Substitute.For<ICloudCredentialStore>();
        store.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Models =
                [
                    .. deploymentNames.Select(static name => new StoredAzureFoundryModel
                    {
                        DeploymentName = name
                    })
                ]
            }
        });
        return store;
    }

    private static CloudModelResolver CreateResolver(ICloudCredentialStore store, IActiveCloudChatClientFactory? routing = null)
    {
        var trust = new ModelTrustResolver(new FakeExternalProviderRegistry(),
            Substitute.For<IExternalProviderRegistryCache>(),
            routing ?? Substitute.For<IActiveCloudChatClientFactory>(),
            NullLogger<ModelTrustResolver>.Instance);
        return new CloudModelResolver(store, trust, NullLogger<CloudModelResolver>.Instance);
    }
}
