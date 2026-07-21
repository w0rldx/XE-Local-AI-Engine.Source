namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Gate 1 proof for the final selected-cloud boundary and the pinned Microsoft.Extensions.AI 10.7.0 function loop.
/// </summary>
public sealed class DevelopmentCloudEgressGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 18, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments("local-only")]
    [Arguments("expired-envelope")]
    [Arguments("missing-bundle")]
    [Arguments("stale-bundle")]
    [Arguments("mismatched-bundle")]
    [Arguments("oversized-bundle")]
    [Arguments("secret-scan-failed")]
    [Arguments("missing-envelope")]
    [Arguments("malformed-purpose")]
    [Arguments("malformed-envelope")]
    public async Task GetResponseAsync_WhenDevelopmentAuthorizationIsInvalid_BlocksBeforeCloudTransport(string scenario)
    {
        using var localClient = new StubChatClient("local");
        using var cloudClient = new StubChatClient("cloud");
        var bundle = BundleState.Valid();
        var authorizer = new FakeCloudEgressAuthorizer(Now, bundle);
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient), () => localClient, authorizer);
        var options = CreateOptions(CreateEnvelope(), scenario);

        await AssertEx.ThrowsAsync<CloudEgressAuthorizationException>(() =>
            runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "sentinel prompt")], options));

        AssertEx.Equal(expected: 0, cloudClient.CallCount, $"{scenario} reached cloud transport");
        AssertEx.Equal(expected: 0, localClient.CallCount, $"{scenario} silently fell back to local");
        AssertEx.Equal(expected: 1, authorizer.CallCount);
    }

    [Test]
    public async Task GetResponseAsync_WhenExecutionContextFlowIsSuppressed_MissingEnvelopeStillBlocks()
    {
        using var localClient = new StubChatClient("local");
        using var cloudClient = new StubChatClient("cloud");
        var authorizer = new FakeCloudEgressAuthorizer(Now, BundleState.Valid());
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient), () => localClient, authorizer);
        var options = CreateOptions(CreateEnvelope(), "missing-envelope");

        Task invoke;
        using (ExecutionContext.SuppressFlow())
        {
            invoke = Task.Run(async () =>
                await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "sentinel prompt")], options).ConfigureAwait(false));
        }

        await AssertEx.ThrowsAsync<CloudEgressAuthorizationException>(() => invoke);
        AssertEx.Equal(expected: 0, cloudClient.CallCount);
        AssertEx.Equal(expected: 1, authorizer.CallCount);
    }

    [Test]
    public async Task GetResponseAsync_WhenDevelopmentRequestRoutesLocal_DoesNotInvokeCloudAuthorizer()
    {
        using var localClient = new StubChatClient("local");
        using var cloudClient = new StubChatClient("cloud");
        var authorizer = new FakeCloudEgressAuthorizer(Now, BundleState.Valid());
        using var runtime = new RuntimeChatClient(new ToggleableCloudFactory(cloudClient), () => localClient, authorizer);
        var options = CreateOptions(CreateEnvelope(policy: DevelopmentExecutionPolicy.LocalOnly));

        await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "local request")], options);

        AssertEx.Equal(expected: 1, localClient.CallCount);
        AssertEx.Equal(expected: 0, cloudClient.CallCount);
        AssertEx.Equal(expected: 0, authorizer.CallCount);
    }

    [Test]
    public async Task GetResponseAsync_WhenSelectionChangesFromLocalToCloud_ReauthorizesAtCloudBoundary()
    {
        using var localClient = new StubChatClient("local");
        using var cloudClient = new StubChatClient("cloud");
        var selector = new ToggleableCloudFactory(cloudClient);
        var authorizer = new FakeCloudEgressAuthorizer(Now, BundleState.Valid());
        using var runtime = new RuntimeChatClient(selector, () => localClient, authorizer);
        var options = CreateOptions(CreateEnvelope());

        selector.CloudActive = false;
        await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "local round")], options);
        selector.CloudActive = true;
        await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "cloud round")], options);

        AssertEx.Equal(expected: 1, localClient.CallCount);
        AssertEx.Equal(expected: 1, cloudClient.CallCount);
        AssertEx.Equal(expected: 1, authorizer.CallCount);
    }

    [Test]
    public async Task GetResponseAsync_WhenAuthorizationIsValid_AllowsCloudAndAuditsMetadataOnly()
    {
        using var localClient = new StubChatClient("local");
        using var cloudClient = new StubChatClient("cloud");
        var authorizer = new FakeCloudEgressAuthorizer(Now, BundleState.Valid());
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient), () => localClient, authorizer);
        var options = CreateOptions(CreateEnvelope());

        await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "raw-secret-sentinel")], options);

        AssertEx.Equal(expected: 1, cloudClient.CallCount);
        AssertEx.Equal(expected: 1, authorizer.CallCount);
        var audit = AssertEx.NotNull(authorizer.Audits.SingleOrDefault());
        AssertEx.Equal("project-1", audit.ProjectId);
        AssertEx.Equal("task-1", audit.TaskId);
        AssertEx.Equal("attempt-1", audit.AttemptId);
        AssertEx.Equal("fake-cloud", audit.ProviderName);
        AssertEx.Equal("cloud-model", audit.ModelId);
        AssertEx.Equal("bundle-hash", audit.BundleHash);
        AssertEx.False(audit.ToString().Contains("raw-secret-sentinel", StringComparison.Ordinal));
    }

    [Test]
    public async Task FunctionLoop_NonStreaming_ForcedCloneAuthorizesBothRawRoundsBeforeTransport()
    {
        var events = new List<string>();
        using var localClient = new StubChatClient("local");
        using var cloudClient = new TwoRoundCloudChatClient(events);
        var authorizer = new FakeCloudEgressAuthorizer(Now, BundleState.Valid(), events);
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient), () => localClient, authorizer);
        using var functionLoop = runtime.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var envelope = CreateEnvelope();
        var options = CreateFunctionOptions(envelope);

        _ = await functionLoop.GetResponseAsync([new ChatMessage(ChatRole.User, "run tool")], options);

        AssertTwoRoundCarrierEvidence(envelope, cloudClient.OptionsByRound, authorizer, events);
    }

    [Test]
    public async Task FunctionLoop_Streaming_ForcedCloneAuthorizesBothRawRoundsBeforeTransport()
    {
        var events = new List<string>();
        using var localClient = new StubChatClient("local");
        using var cloudClient = new TwoRoundCloudChatClient(events);
        var authorizer = new FakeCloudEgressAuthorizer(Now, BundleState.Valid(), events);
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient), () => localClient, authorizer);
        using var functionLoop = runtime.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var envelope = CreateEnvelope();
        var options = CreateFunctionOptions(envelope);

        await foreach (var update in functionLoop.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "run tool")], options))
        {
            GC.KeepAlive(update);
        }

        AssertTwoRoundCarrierEvidence(envelope, cloudClient.OptionsByRound, authorizer, events);
    }

    [Test]
    public async Task FunctionLoop_WhenCarrierIsRemovedAfterFirstRound_BlocksSecondRoundBeforeTransport()
    {
        var events = new List<string>();
        using var localClient = new StubChatClient("local");
        using var cloudClient = new TwoRoundCloudChatClient(events, removeCarrierAfterFirstRound: true);
        var authorizer = new FakeCloudEgressAuthorizer(Now, BundleState.Valid(), events);
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient), () => localClient, authorizer);
        using var functionLoop = runtime.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var options = CreateFunctionOptions(CreateEnvelope());

        await AssertEx.ThrowsAsync<CloudEgressAuthorizationException>(() =>
            functionLoop.GetResponseAsync([new ChatMessage(ChatRole.User, "run tool")], options));

        AssertEx.Equal(expected: 1, cloudClient.TransportCount);
        AssertEx.Equal(expected: 2, authorizer.CallCount);
        AssertEx.Equal("authorize1,transport1,authorize2", string.Join(',', events));
    }

    [Test]
    public async Task FunctionLoop_Streaming_WhenCarrierIsRemovedAfterFirstRound_BlocksSecondRoundBeforeTransport()
    {
        var events = new List<string>();
        using var localClient = new StubChatClient("local");
        using var cloudClient = new TwoRoundCloudChatClient(events, removeCarrierAfterFirstRound: true);
        var authorizer = new FakeCloudEgressAuthorizer(Now, BundleState.Valid(), events);
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient), () => localClient, authorizer);
        using var functionLoop = runtime.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var options = CreateFunctionOptions(CreateEnvelope());

        await AssertEx.ThrowsAsync<CloudEgressAuthorizationException>(async () =>
        {
            await foreach (var update in functionLoop.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "run tool")], options))
            {
                GC.KeepAlive(update);
            }
        });

        AssertEx.Equal(expected: 1, cloudClient.TransportCount);
        AssertEx.Equal(expected: 2, authorizer.CallCount);
        AssertEx.Equal("authorize1,transport1,authorize2", string.Join(',', events));
    }

    [Test]
    public async Task GetStreamingResponseAsync_WhenEnvelopeIsMissing_BlocksBeforeCloudEnumeration()
    {
        using var localClient = new StubChatClient("local");
        using var cloudClient = new StubChatClient("cloud");
        var authorizer = new FakeCloudEgressAuthorizer(Now, BundleState.Valid());
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient), () => localClient, authorizer);
        var options = CreateOptions(CreateEnvelope(), "missing-envelope");

        await AssertEx.ThrowsAsync<CloudEgressAuthorizationException>(async () =>
        {
            await foreach (var update in runtime.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "sentinel prompt")], options))
            {
                GC.KeepAlive(update);
            }
        });

        AssertEx.Equal(expected: 0, cloudClient.CallCount);
        AssertEx.Equal(expected: 1, authorizer.CallCount);
    }

    private static ChatOptions CreateOptions(DevelopmentCloudAuthorizationEnvelope envelope, string? scenario = null)
    {
        var options = new ChatOptions
        {
            ModelId = "cloud-model"
        };
        DevelopmentCloudAuthorizationMetadata.Apply(options, envelope);

        switch (scenario)
        {
            case null:
                break;
            case "local-only":
                ReplaceEnvelope(options, CreateEnvelope(policy: DevelopmentExecutionPolicy.LocalOnly));
                break;
            case "expired-envelope":
                ReplaceEnvelope(options, CreateEnvelope(expiresAt: Now.AddSeconds(-1)));
                break;
            case "missing-bundle":
                ReplaceEnvelope(options, CreateEnvelope(bundleId: null, bundleHash: null));
                break;
            case "stale-bundle":
                ReplaceEnvelope(options, CreateEnvelope(bundleId: "stale-bundle"));
                break;
            case "mismatched-bundle":
                ReplaceEnvelope(options, CreateEnvelope(bundleHash: "different-hash"));
                break;
            case "oversized-bundle":
                ReplaceEnvelope(options, CreateEnvelope(bundleId: "oversized-bundle"));
                break;
            case "secret-scan-failed":
                ReplaceEnvelope(options, CreateEnvelope(bundleId: "unsafe-bundle"));
                break;
            case "missing-envelope":
                options.AdditionalProperties!.Remove(DevelopmentCloudAuthorizationMetadata.EnvelopeKey);
                break;
            case "malformed-purpose":
                options.AdditionalProperties![DevelopmentCloudAuthorizationMetadata.PurposeKey] = "chat";
                break;
            case "malformed-envelope":
                options.AdditionalProperties![DevelopmentCloudAuthorizationMetadata.EnvelopeKey] = "not-an-envelope";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown authorization scenario.");
        }

        return options;
    }

    private static ChatOptions CreateFunctionOptions(DevelopmentCloudAuthorizationEnvelope envelope)
    {
        var tool = AIFunctionFactory.Create(() => "tool-result", "gate1_probe", "Gate 1 deterministic probe tool.");
        var options = CreateOptions(envelope);
        options.Tools = [tool];
        return options;
    }

    private static DevelopmentCloudAuthorizationEnvelope CreateEnvelope(DevelopmentExecutionPolicy policy = DevelopmentExecutionPolicy.CloudScoped,
        string? bundleId = "bundle-1",
        string? bundleHash = "bundle-hash",
        DateTimeOffset? expiresAt = null)
    {
        return new DevelopmentCloudAuthorizationEnvelope(DevelopmentCloudAuthorizationEnvelope.CurrentVersion,
            "project-1",
            "task-1",
            "attempt-1",
            policy,
            bundleId,
            bundleHash,
            expiresAt ?? Now.AddMinutes(5),
            "nonce-1");
    }

    private static void ReplaceEnvelope(ChatOptions options, DevelopmentCloudAuthorizationEnvelope envelope)
    {
        options.AdditionalProperties![DevelopmentCloudAuthorizationMetadata.EnvelopeKey] = envelope;
    }

    private static void AssertTwoRoundCarrierEvidence(DevelopmentCloudAuthorizationEnvelope expectedEnvelope,
        IReadOnlyList<ChatOptions> optionsByRound,
        FakeCloudEgressAuthorizer authorizer,
        IReadOnlyList<string> events)
    {
        AssertEx.Equal(expected: 2, optionsByRound.Count);
        AssertEx.Equal(expected: 2, authorizer.CallCount);
        AssertEx.False(ReferenceEquals(optionsByRound[0], optionsByRound[1]), "MEAI clone branch was not forced");
        AssertEx.False(ReferenceEquals(optionsByRound[0].AdditionalProperties, optionsByRound[1].AdditionalProperties),
            "AdditionalProperties dictionary was not cloned");
        AssertEx.True(ReferenceEquals(expectedEnvelope,
            optionsByRound[0].AdditionalProperties![DevelopmentCloudAuthorizationMetadata.EnvelopeKey]));
        AssertEx.True(ReferenceEquals(expectedEnvelope,
            optionsByRound[1].AdditionalProperties![DevelopmentCloudAuthorizationMetadata.EnvelopeKey]));
        AssertEx.Equal("authorize1,transport1,authorize2,transport2", string.Join(',', events));
    }

    private sealed class FakeCloudEgressAuthorizer : ICloudEgressAuthorizer
    {
        private const long MaximumBundleBytes = 1024;
        private readonly IReadOnlyDictionary<string, BundleState> _bundles;
        private readonly List<string>? _events;
        private readonly DateTimeOffset _now;

        public FakeCloudEgressAuthorizer(DateTimeOffset now, BundleState primaryBundle, List<string>? events = null)
        {
            _now = now;
            _events = events;
            _bundles = new Dictionary<string, BundleState>(StringComparer.Ordinal)
            {
                [primaryBundle.Id] = primaryBundle,
                ["stale-bundle"] = BundleState.Valid("stale-bundle") with { ExpiresAt = now.AddSeconds(-1) },
                ["oversized-bundle"] = BundleState.Valid("oversized-bundle") with { SizeBytes = MaximumBundleBytes + 1 },
                ["unsafe-bundle"] = BundleState.Valid("unsafe-bundle") with { SecretScanPassed = false }
            };
        }

        public int CallCount { get; private set; }
        public List<AuditRecord> Audits { get; } = [];

        public void Authorize(CloudEgressAuthorizationRequest request)
        {
            CallCount++;
            _events?.Add($"authorize{CallCount}");

            Reject(request.CarrierState != CloudEgressAuthorizationCarrierState.Valid, $"carrier-{request.CarrierState}");
            var envelope = request.Envelope ?? throw new CloudEgressAuthorizationException("missing-envelope");
            Reject(envelope.Version != DevelopmentCloudAuthorizationEnvelope.CurrentVersion, "unsupported-version");
            Reject(string.IsNullOrWhiteSpace(envelope.ProjectId)
                   || string.IsNullOrWhiteSpace(envelope.TaskId)
                   || string.IsNullOrWhiteSpace(envelope.AttemptId)
                   || string.IsNullOrWhiteSpace(envelope.Nonce), "malformed-envelope");
            Reject(envelope.ExpiresAt <= _now, "expired-envelope");
            Reject(envelope.Policy == DevelopmentExecutionPolicy.LocalOnly, "local-only");
            Reject(string.IsNullOrWhiteSpace(envelope.AuthorizedBundleId)
                   || string.IsNullOrWhiteSpace(envelope.AuthorizedBundleHash), "missing-bundle");
            Reject(!_bundles.TryGetValue(envelope.AuthorizedBundleId!, out var bundle), "missing-bundle");
            Reject(bundle!.ExpiresAt <= _now, "stale-bundle");
            Reject(!string.Equals(bundle.Hash, envelope.AuthorizedBundleHash, StringComparison.Ordinal), "mismatched-bundle");
            Reject(bundle.SizeBytes > MaximumBundleBytes, "oversized-bundle");
            Reject(!bundle.SecretScanPassed, "secret-scan-failed");

            Audits.Add(new AuditRecord(envelope.ProjectId,
                envelope.TaskId,
                envelope.AttemptId,
                request.ProviderName,
                request.ModelId,
                envelope.AuthorizedBundleHash!));
        }

        private static void Reject(bool condition, string reason)
        {
            if (condition)
            {
                throw new CloudEgressAuthorizationException(reason);
            }
        }
    }

    private sealed record BundleState(string Id, string Hash, long SizeBytes, bool SecretScanPassed, DateTimeOffset ExpiresAt)
    {
        public static BundleState Valid(string id = "bundle-1")
        {
            return new BundleState(id, "bundle-hash", SizeBytes: 256, SecretScanPassed: true, Now.AddMinutes(10));
        }
    }

    private sealed record AuditRecord(string ProjectId,
        string TaskId,
        string AttemptId,
        string ProviderName,
        string? ModelId,
        string BundleHash);

    private sealed class FixedCloudFactory(IChatClient cloudClient) : IActiveCloudChatClientFactory
    {
        public bool TryCreateActiveCloudChatClient(string? requestedModelId, out IChatClient? client)
        {
            client = cloudClient;
            return true;
        }

        public bool IsCloudProviderSelected(string? requestedModelId = null)
        {
            return true;
        }

        public string? ResolveActiveCloudProviderName(string? requestedModelId = null)
        {
            return "fake-cloud";
        }

        public void InvalidateSelectionCache()
        {
        }
    }

    private sealed class ToggleableCloudFactory(IChatClient cloudClient) : IActiveCloudChatClientFactory
    {
        public bool CloudActive { get; set; }

        public bool TryCreateActiveCloudChatClient(string? requestedModelId, out IChatClient? client)
        {
            client = CloudActive ? cloudClient : null;
            return CloudActive;
        }

        public bool IsCloudProviderSelected(string? requestedModelId = null)
        {
            return CloudActive;
        }

        public string? ResolveActiveCloudProviderName(string? requestedModelId = null)
        {
            return CloudActive ? "fake-cloud" : null;
        }

        public void InvalidateSelectionCache()
        {
        }
    }

    private sealed class TwoRoundCloudChatClient(List<string> events, bool removeCarrierAfterFirstRound = false) : IChatClient
    {
        public List<ChatOptions> OptionsByRound { get; } = [];
        public int TransportCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var capturedOptions = AssertEx.NotNull(options);
            RecordTransport(capturedOptions);
            if (TransportCount == 1)
            {
                RemoveCarrierIfRequested(capturedOptions);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "gate1_probe", new Dictionary<string, object?>())]))
                {
                    ConversationId = "conversation-1"
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var capturedOptions = AssertEx.NotNull(options);
            RecordTransport(capturedOptions);
            await Task.Yield();

            if (TransportCount == 1)
            {
                RemoveCarrierIfRequested(capturedOptions);
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "gate1_probe", new Dictionary<string, object?>())])
                {
                    ConversationId = "conversation-1"
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
        }

        private void RecordTransport(ChatOptions options)
        {
            TransportCount++;
            OptionsByRound.Add(options);
            events.Add($"transport{TransportCount}");
        }

        private void RemoveCarrierIfRequested(ChatOptions options)
        {
            if (removeCarrierAfterFirstRound)
            {
                options.AdditionalProperties!.Remove(DevelopmentCloudAuthorizationMetadata.EnvelopeKey);
            }
        }
    }
}
