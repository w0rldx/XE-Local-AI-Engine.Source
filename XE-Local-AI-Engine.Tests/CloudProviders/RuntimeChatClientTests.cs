namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
/// Proves the runtime-switch property: the registered <see cref="RuntimeChatClient"/>
/// is captured once by singleton consumers, yet each send re-selects cloud-vs-local — so signing in routes the
/// NEXT send to the cloud without a restart, and signing out routes the next send back to local.
/// </summary>
public sealed class RuntimeChatClientTests
{
    [Test]
    public async Task GetResponse_WhenCloudBecomesActiveBetweenSends_RoutesToCloudWithoutReconstruction()
    {
        using var localClient = new StubChatClient("local");
        using var cloudClient = new StubChatClient("cloud");
        var selector = new ToggleableCloudFactory(cloudClient);
        using var runtime = new RuntimeChatClient(selector, () => localClient);

        // Signed out → routes local.
        selector.CloudActive = false;
        await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        AssertEx.Equal(1, localClient.CallCount);
        AssertEx.Equal(0, cloudClient.CallCount);

        // Sign in at runtime → next send routes cloud, same wrapper instance (no restart).
        selector.CloudActive = true;
        await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        AssertEx.Equal(1, cloudClient.CallCount);
        AssertEx.Equal(1, localClient.CallCount);

        // Sign out again → next send routes local again.
        selector.CloudActive = false;
        await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        AssertEx.Equal(2, localClient.CallCount);
        AssertEx.Equal(1, cloudClient.CallCount);
    }

    [Test]
    public async Task GetResponse_WhenCloudSelectedButFactoryThrowsReauth_PropagatesNotLocalFallback()
    {
        using var localClient = new StubChatClient("local");
        var selector = new ThrowingCloudFactory();
        using var runtime = new RuntimeChatClient(selector, () => localClient);

        await AssertEx.ThrowsAsync<InvalidOperationException>(
            () => runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        // The local client must NOT have been used — a selected-but-unusable cloud provider does not silently fall back.
        AssertEx.Equal(0, localClient.CallCount);
    }

    /// <summary>A selector whose cloud-vs-local decision the test flips between sends.</summary>
    private sealed class ToggleableCloudFactory : IActiveCloudChatClientFactory
    {
        private readonly IChatClient _cloudClient;

        public ToggleableCloudFactory(IChatClient cloudClient) => _cloudClient = cloudClient;

        public bool CloudActive { get; set; }

        public bool TryCreateActiveCloudChatClient(out IChatClient? client)
        {
            client = CloudActive ? _cloudClient : null;
            return CloudActive;
        }

        public bool IsCloudProviderSelected() => CloudActive;

        public void InvalidateSelectionCache()
        {
        }
    }

    /// <summary>A selector that is "selected" but throws on build (the unauthenticated-Codex re-auth case).</summary>
    private sealed class ThrowingCloudFactory : IActiveCloudChatClientFactory
    {
        public bool TryCreateActiveCloudChatClient(out IChatClient? client)
            => throw new InvalidOperationException("cloud not authenticated");

        public bool IsCloudProviderSelected() => true;

        public void InvalidateSelectionCache()
        {
        }
    }
}
