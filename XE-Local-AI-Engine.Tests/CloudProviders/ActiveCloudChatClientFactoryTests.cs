namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the active-cloud selector: Codex selection keys off live-session
///     presence, Azure off the persisted credential, the typed re-auth error surfaces (not a 500) when Codex is selected
///     but unusable, the fingerprint client-cache rebuilds only on selection change, swapped-out clients are NOT
///     disposed (concurrency-safety), and the selection snapshot keeps the token-store read off the per-send hot path.
///     A second block of tests covers per-request routing: an explicit <c>requestedModelId</c> (the caller's
///     <c>ChatOptions.ModelId</c>) picks its OWN provider independent of — and, for an Azure deployment match, even
///     ahead of — the node-default selection.
/// </summary>
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
    Justification = "The out client aliases a test-owned StubChatClient already disposed via 'using'.")]
public sealed class ActiveCloudChatClientFactoryTests
{
    private static readonly TimeSpan PastTtl = TimeSpan.FromSeconds(5);

    [Test]
    public void TryCreate_WhenCodexSessionPresent_ReturnsCodexClient()
    {
        using var codexClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(NonExpiredSession());
        harness.CodexFactory.Create(Arg.Any<string?>()).Returns(codexClient);

        var produced = harness.Factory.TryCreateActiveCloudChatClient(null, out var client);

        AssertEx.True(produced);
        AssertEx.True(ReferenceEquals(codexClient, client));
    }

    [Test]
    public void TryCreate_WhenNodeDefaultIsCodexModel_BuildsCodexClientWithThatModelId()
    {
        using var codexClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(NonExpiredSession());
        harness.NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(new StoredNodeSettings
               {
                   DefaultModelName = "gpt-5.4"
               });
        harness.CodexFactory.Create(Arg.Any<string?>()).Returns(codexClient);

        harness.Factory.TryCreateActiveCloudChatClient(null, out _);

        // The selected Codex model id must reach the Codex factory — never always-default.
        harness.CodexFactory.Received().Create("gpt-5.4");
    }

    [Test]
    public void TryCreate_WhenNodeDefaultIsLocalModel_BuildsCodexClientWithConfiguredDefault()
    {
        using var codexClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(NonExpiredSession());
        // A local Ollama model name left in the node default must NOT reach Codex as the model — fall back to default.
        harness.NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(new StoredNodeSettings
               {
                   DefaultModelName = "qwen3:8b"
               });
        harness.CodexFactory.Create(Arg.Any<string?>()).Returns(codexClient);

        harness.Factory.TryCreateActiveCloudChatClient(null, out _);

        harness.CodexFactory.Received().Create(harness.CodexOptions.DefaultModel);
        harness.CodexFactory.DidNotReceive().Create("qwen3:8b");
    }

    [Test]
    public void TryCreate_WhenNoCodexSession_ButAzureDeploymentSelected_ReturnsAzureClient()
    {
        using var azureClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>())
               .Returns(CreateAzureConfig());
        // Selected-model-driven (HIGH-1): the node-default must match a stored deployment for Azure to be selected.
        harness.NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(new StoredNodeSettings
               {
                   DefaultModelName = "gpt-4o"
               });
        harness.AzureFactory.Create(Arg.Any<StoredAzureFoundryConnection>(), "gpt-4o").Returns(azureClient);

        var produced = harness.Factory.TryCreateActiveCloudChatClient(null, out var client);

        AssertEx.True(produced);
        AssertEx.True(ReferenceEquals(azureClient, client));
    }

    [Test]
    public void TryCreate_WhenAzureConnectionSaved_ButLocalModelSelected_RoutesLocal()
    {
        // HIGH-1 regression guard: a saved Azure connection alone must NOT force cloud when a local model is selected.
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>())
               .Returns(CreateAzureConfig());
        harness.NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(new StoredNodeSettings
               {
                   DefaultModelName = "qwen3:8b"
               });

        var produced = harness.Factory.TryCreateActiveCloudChatClient(null, out var client);

        AssertEx.False(produced);
        AssertEx.Null(client);
    }

    [Test]
    public void TryCreate_WhenNoCloudSelection_ReturnsFalseForLocalRouting()
    {
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        harness.CredentialStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((StoredCloudCredentials?)null);

        var produced = harness.Factory.TryCreateActiveCloudChatClient(null, out var client);

        AssertEx.False(produced);
        AssertEx.Null(client);
    }

    [Test]
    public void TryCreate_WhenCodexSelectedButUnusable_SurfacesTypedReauthError_NotGenericFailure()
    {
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(NonExpiredSession());
        // The factory itself decides the session is unusable and throws the typed AuthRequired error.
        harness.CodexFactory.Create(Arg.Any<string?>())
               .Returns(_ => throw new CodexProviderException(CodexProviderErrorKind.AuthRequired, "sign in required"));

        var error = Throws<CodexProviderException>(() => harness.Factory.TryCreateActiveCloudChatClient(null, out _));

        AssertEx.Equal(CodexProviderErrorKind.AuthRequired, error.Kind);
    }

    [Test]
    public void IsCloudProviderSelected_TrueWhenCodexSessionPresent()
    {
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(NonExpiredSession());

        AssertEx.True(harness.Factory.IsCloudProviderSelected());
    }

    [Test]
    public void TryCreate_WhenSelectionUnchanged_ReusesCachedClient_WithoutRebuildingOrReReadingStore()
    {
        using var codexClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(SessionExpiringAt(2030));
        harness.CodexFactory.Create(Arg.Any<string?>()).Returns(codexClient);
        using var factory = harness.Factory;

        factory.TryCreateActiveCloudChatClient(null, out var first);
        factory.TryCreateActiveCloudChatClient(null, out var second);
        factory.TryCreateActiveCloudChatClient(null, out var third);

        AssertEx.True(ReferenceEquals(first, second));
        AssertEx.True(ReferenceEquals(second, third));
        // The expensive build ran exactly once despite three resolutions (fingerprint unchanged).
        harness.CodexFactory.Received(1).Create(Arg.Any<string?>());
        // The selection snapshot kept the encrypted token-store read off the hot path (only one read for 3 sends).
        harness.CodexTokenStore.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void TryCreate_WhenSessionRefreshed_RebuildsClient_AndDoesNotDisposeThePrevious()
    {
        using var firstClient = new StubChatClient();
        using var secondClient = new StubChatClient();
        var harness = new Harness();
        // First resolution: an early expiry. Second: a refreshed session (later expiry) → fingerprint changes.
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(SessionExpiringAt(2030),
            SessionExpiringAt(year: 2030, hour: 1, "acct2"));
        harness.CodexFactory.Create(Arg.Any<string?>()).Returns(firstClient, secondClient);
        using var factory = harness.Factory;

        factory.TryCreateActiveCloudChatClient(null, out var before);
        harness.Time.Advance(PastTtl); // lapse the selection snapshot so the refreshed session is re-read
        factory.TryCreateActiveCloudChatClient(null, out var after);

        AssertEx.True(ReferenceEquals(firstClient, before));
        AssertEx.True(ReferenceEquals(secondClient, after));
        harness.CodexFactory.Received(2).Create(Arg.Any<string?>());
        // CRITICAL: the swapped-out client is NOT disposed — a concurrent request may still be streaming on it.
        AssertEx.False(firstClient.IsDisposed, "swapped-out cloud client must NOT be disposed (use-after-dispose race)");
        AssertEx.False(secondClient.IsDisposed, "the now-active client must not be disposed");
    }

    [Test]
    public void TryCreate_WhenSignedOutAfterCaching_RoutesLocal_WithoutDisposingTheCachedClient()
    {
        using var codexClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(NonExpiredSession(),
            (CodexTokens?)null);
        harness.CredentialStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((StoredCloudCredentials?)null);
        harness.CodexFactory.Create(Arg.Any<string?>()).Returns(codexClient);
        using var factory = harness.Factory;

        factory.TryCreateActiveCloudChatClient(null, out _); // signed in → cached
        factory.InvalidateSelectionCache(); // logout pokes the selector
        var produced = factory.TryCreateActiveCloudChatClient(null, out var afterLogout); // signed out → local

        AssertEx.False(produced);
        AssertEx.Null(afterLogout);
        // Swapped-out on sign-out: forgotten but NOT disposed (a concurrent request may still hold it).
        AssertEx.False(codexClient.IsDisposed, "cached cloud client must NOT be disposed on sign-out (race-safe)");
    }

    [Test]
    public void InvalidateSelectionCache_ForcesAFreshStoreReadOnNextResolve()
    {
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        using var factory = harness.Factory;

        factory.TryCreateActiveCloudChatClient(null, out _);
        factory.InvalidateSelectionCache();
        factory.TryCreateActiveCloudChatClient(null, out _);

        // Without invalidation the 3s snapshot would have served the second call from cache (1 read); the explicit
        // invalidation forces a second read.
        harness.CodexTokenStore.Received(2).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StreamHeldInFlight_WhileSelectionSwaps_IsNotDisposedAndCompletes()
    {
        // CRITICAL, deterministic proof: hold a streaming enumeration OPEN on the resolved cloud client, then
        // flip the selection (refresh) so the factory swaps the cached client WHILE the stream is live. Under the
        // pre-fix dispose-on-swap behaviour this would ObjectDispose the in-flight client; the fix (no dispose on
        // swap) must let the stream complete with no exception.
        using var swapHappened = new SemaphoreSlim(initialCount: 0, maxCount: 1);
        using var resumeStream = new SemaphoreSlim(initialCount: 0, maxCount: 1);

        // The first resolved Codex client blocks mid-stream until the test has performed the swap; later clients
        // (after the refresh) do not block.
        var createdCount = 0;
        var firstClient = new StubChatClient(midStreamGate: async () =>
        {
            swapHappened.Release(); // signal the test that we're parked mid-stream
            await resumeStream.WaitAsync(); // wait until the test has flipped + resolved the new selection
        });
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(SessionExpiringAt(2030),
            SessionExpiringAt(year: 2030, hour: 1, "acct2"));
        harness.CodexFactory.Create(Arg.Any<string?>())
               .Returns(_ => Interlocked.Increment(ref createdCount) == 1 ? firstClient : new StubChatClient());
        using var factory = harness.Factory;

        factory.TryCreateActiveCloudChatClient(null, out var client);
        var inFlight = AssertEx.NotNull(client);

        // Start draining the stream on a background task; it will park at the mid-stream gate.
        ObjectDisposedException? streamError = null;
        var streamTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in inFlight.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
                {
                    // drain
                }
            }
            catch (ObjectDisposedException ex)
            {
                streamError = ex;
            }
        });

        await swapHappened.WaitAsync(); // the stream is now provably parked mid-enumeration
        harness.Time.Advance(PastTtl); // lapse the snapshot so the refreshed session is re-read
        factory.TryCreateActiveCloudChatClient(null, out var afterSwap); // swaps the cache → would dispose firstClient pre-fix
        resumeStream.Release(); // let the held stream finish
        await streamTask;

        AssertEx.Null(streamError, "the in-flight stream must not see ObjectDisposedException after a swap");
        AssertEx.False(firstClient.IsDisposed, "the swapped-out, still-in-flight client must not be disposed");
        AssertEx.True(AssertEx.NotNull(afterSwap) is not null);
    }

    [Test]
    public async Task TryCreate_WhenBuildIsSlow_DoesNotSerializeConcurrentCalls()
    {
        // Regression guard: Build() must run OUTSIDE the cache lock so a slow build (e.g. Entra ID
        // InteractiveBrowserCredential's synchronous first-use browser sign-in) never blocks an unrelated concurrent
        // send waiting on the lock. Proven by two concurrent calls both entering the factory before either returns —
        // under the pre-fix code (Build() called while holding _cacheGate) this deadlocks and the test times out.
        using var stubClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateAzureConfig());
        harness.NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(new StoredNodeSettings
               {
                   DefaultModelName = "gpt-4o"
               });

        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentEntries = 0;
        harness.AzureFactory.Create(Arg.Any<StoredAzureFoundryConnection>(), "gpt-4o").Returns(_ =>
        {
            if (Interlocked.Increment(ref concurrentEntries) >= 2)
            {
                bothEntered.TrySetResult();
            }

            // Blocks until BOTH concurrent calls have entered Create(), proving neither call is serialized behind
            // the other's cache-gate lock while its build is in flight.
            bothEntered.Task.Wait(TimeSpan.FromSeconds(5));
            return stubClient;
        });
        using var factory = harness.Factory;

        var task1 = Task.Run(() => factory.TryCreateActiveCloudChatClient(null, out _));
        var task2 = Task.Run(() => factory.TryCreateActiveCloudChatClient(null, out _));
        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(2, concurrentEntries);
    }

    [Test]
    public async Task ConcurrentSends_WhileSelectionFlips_NeverThrowObjectDisposed()
    {
        // Broad stress companion to the deterministic test above: many parallel sends while the fingerprint flips.
        var harness = new Harness();
        var flip = 0;
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(_ => SessionExpiringAt(year: 2030, Interlocked.Increment(ref flip) % 4));
        harness.CodexFactory.Create(Arg.Any<string?>()).Returns(_ => new StubChatClient());
        using var factory = harness.Factory;

        var failures = 0;
        var tasks = Enumerable.Range(start: 0, count: 64).Select(_ => Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 25; i++)
                {
                    factory.InvalidateSelectionCache(); // force re-read so fingerprints keep flipping
                    if (factory.TryCreateActiveCloudChatClient(null, out var client) && client is not null)
                    {
                        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
                        {
                            // drain — the client must not be disposed underneath the enumeration
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Increment(ref failures);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        AssertEx.Equal(expected: 0, failures);
    }

    [Test]
    public void TryCreate_WhenRequestedModelIsAzureDeployment_RoutesAzure_EvenThoughNodeDefaultIsLocal()
    {
        // The regression this whole change fixes: the chat model dropdown picks an Azure deployment while the node
        // default still names a local model — the send must route Azure, not fall through to llama-server.
        using var azureClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateAzureConfig());
        harness.NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(new StoredNodeSettings
               {
                   DefaultModelName = "qwen3:8b"
               });
        harness.AzureFactory.Create(Arg.Any<StoredAzureFoundryConnection>(), "gpt-4o").Returns(azureClient);

        var produced = harness.Factory.TryCreateActiveCloudChatClient("gpt-4o", out var client);

        AssertEx.True(produced);
        AssertEx.True(ReferenceEquals(azureClient, client));
    }

    [Test]
    public void TryCreate_WhenRequestedModelIsAzureDeployment_RoutesAzure_EvenWithAnActiveCodexSession()
    {
        // Precedence rule: an explicit Azure deployment pick wins even over a live Codex session — switching the
        // model dropdown to Azure must not require signing out of Codex first.
        using var azureClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(NonExpiredSession());
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateAzureConfig());
        harness.AzureFactory.Create(Arg.Any<StoredAzureFoundryConnection>(), "gpt-4o").Returns(azureClient);

        var produced = harness.Factory.TryCreateActiveCloudChatClient("gpt-4o", out var client);

        AssertEx.True(produced);
        AssertEx.True(ReferenceEquals(azureClient, client));
        harness.CodexFactory.DidNotReceive().Create(Arg.Any<string?>());
    }

    [Test]
    public void TryCreate_WhenRequestedModelIsLocal_RoutesLocal_EvenThoughAzureConnectionExists()
    {
        // The inverse regression guard: an explicit LOCAL model pick must route local even while an Azure connection
        // is saved and would otherwise be picked up by the node-default fallback.
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateAzureConfig());
        harness.NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(new StoredNodeSettings
               {
                   DefaultModelName = "gpt-4o"
               });

        var produced = harness.Factory.TryCreateActiveCloudChatClient("qwen3:8b", out var client);

        AssertEx.False(produced);
        AssertEx.Null(client);
    }

    [Test]
    public void TryCreate_WhenNoRequestedModelId_KeepsNodeDefaultBehavior()
    {
        // A caller that omits ModelId (an agent/flow participant with no pinned model) still gets the pre-existing
        // node-default-driven selection, byte-identical to the old no-argument overload.
        using var azureClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateAzureConfig());
        harness.NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(new StoredNodeSettings
               {
                   DefaultModelName = "gpt-4o"
               });
        harness.AzureFactory.Create(Arg.Any<StoredAzureFoundryConnection>(), "gpt-4o").Returns(azureClient);

        var produced = harness.Factory.TryCreateActiveCloudChatClient(requestedModelId: null, out var client);

        AssertEx.True(produced);
        AssertEx.True(ReferenceEquals(azureClient, client));
    }

    [Test]
    public void TryCreate_WhenRequestedModelIsACodexModel_RoutesCodexWithThatModel_RegardlessOfNodeDefault()
    {
        using var codexClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(NonExpiredSession());
        harness.NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns(new StoredNodeSettings
               {
                   DefaultModelName = "qwen3:8b"
               });
        harness.CodexFactory.Create("gpt-5.4-mini").Returns(codexClient);

        var produced = harness.Factory.TryCreateActiveCloudChatClient("gpt-5.4-mini", out var client);

        AssertEx.True(produced);
        AssertEx.True(ReferenceEquals(codexClient, client));
    }

    [Test]
    public void TryCreate_WhenRequestedModelIsAzureDeployment_MatchIsCaseInsensitive()
    {
        using var azureClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateAzureConfig());
        harness.AzureFactory.Create(Arg.Any<StoredAzureFoundryConnection>(), "gpt-4o").Returns(azureClient);

        var produced = harness.Factory.TryCreateActiveCloudChatClient("GPT-4O", out var client);

        AssertEx.True(produced);
        AssertEx.True(ReferenceEquals(azureClient, client));
    }

    [Test]
    public void TryCreate_WhenRequestedModelIsAnOrphanedId_RoutesLocal()
    {
        // Orphan guard: a stale id that matches neither a stored Azure deployment nor the Codex catalog (e.g. an
        // Azure deployment removed after it was selected) routes local rather than throwing or silently picking
        // some other cloud provider.
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateAzureConfig());

        var produced = harness.Factory.TryCreateActiveCloudChatClient("gpt-4o-removed", out var client);

        AssertEx.False(produced);
        AssertEx.Null(client);
    }

    [Test]
    public void TryCreate_AlternatingBetweenTwoRequestedModels_CachesBothWithoutRebuildingEitherOnAlternation()
    {
        // Cache-shape regression guard: with the per-request model varying send to send, the client cache must be
        // keyed per selection identity (not a single slot) so alternating between two known models does not thrash —
        // each identity's build runs exactly once despite repeated alternation.
        using var azureClient = new StubChatClient();
        using var codexClient = new StubChatClient();
        var harness = new Harness();
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(NonExpiredSession());
        harness.CredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateAzureConfig());
        harness.AzureFactory.Create(Arg.Any<StoredAzureFoundryConnection>(), "gpt-4o").Returns(azureClient);
        harness.CodexFactory.Create("gpt-5.4-mini").Returns(codexClient);
        using var factory = harness.Factory;

        for (var i = 0; i < 3; i++)
        {
            factory.TryCreateActiveCloudChatClient("gpt-4o", out var azureResult);
            factory.TryCreateActiveCloudChatClient("gpt-5.4-mini", out var codexResult);
            AssertEx.True(ReferenceEquals(azureClient, azureResult));
            AssertEx.True(ReferenceEquals(codexClient, codexResult));
        }

        harness.AzureFactory.Received(1).Create(Arg.Any<StoredAzureFoundryConnection>(), "gpt-4o");
        harness.CodexFactory.Received(1).Create("gpt-5.4-mini");
    }

    private static StoredCloudProviderConfig CreateAzureConfig()
    {
        return new StoredCloudProviderConfig
        {
            ProviderName = CloudProviderOptions.ProviderAzureFoundry,
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = "https://example.openai.azure.com/",
                AuthMode = AzureFoundryAuthMode.ApiKey,
                ApiKey = "test-api-key",
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

    private static CodexTokens NonExpiredSession()
    {
        return new CodexTokens("a", "r", DateTimeOffset.UtcNow.AddHours(1), "acct");
    }

    private static CodexTokens SessionExpiringAt(int year, int hour = 0, string accountId = "acct")
    {
        return new CodexTokens("a", "r", new DateTimeOffset(year, month: 1, day: 1, hour, minute: 0, second: 0, TimeSpan.Zero), accountId);
    }

    private static TException Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }

        throw new AssertionException($"Expected {typeof(TException).Name} but no exception was thrown.");
    }

    private sealed class Harness
    {
        public Harness()
        {
            // Default: no operator node-default model selected, so the Codex client resolves to CodexOptions.DefaultModel.
            // Tests that exercise selection plumbing override this return.
            NodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        }

        public ICodexTokenStore CodexTokenStore { get; } = Substitute.For<ICodexTokenStore>();

        public ICloudCredentialStore CredentialStore { get; } = Substitute.For<ICloudCredentialStore>();

        public IAzureFoundryChatClientFactory AzureFactory { get; } = Substitute.For<IAzureFoundryChatClientFactory>();

        public ICodexOAuthChatClientFactory CodexFactory { get; } = Substitute.For<ICodexOAuthChatClientFactory>();

        public INodeSettingsStore NodeSettingsStore { get; } = Substitute.For<INodeSettingsStore>();

        public CodexOptions CodexOptions { get; } = new();

        public AdvanceableTimeProvider Time { get; } = new();

        public ActiveCloudChatClientFactory Factory =>
            new(CodexTokenStore,
                CredentialStore,
                AzureFactory,
                new Lazy<ICodexOAuthChatClientFactory>(() => CodexFactory),
                Options.Create(CodexOptions),
                NodeSettingsStore,
                Time);
    }

    /// <summary>A <see cref="TimeProvider" /> whose clock the test advances to lapse the selection-snapshot TTL.</summary>
    private sealed class AdvanceableTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _now = new(year: 2030, month: 1, day: 1, hour: 0, minute: 0, second: 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan delta)
        {
            lock (_gate)
            {
                _now += delta;
            }
        }
    }
}
