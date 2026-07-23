namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevelopmentCloudScopedSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void Build_WhenInputChangesAfterApproval_PreservesImmutableContentAndHash()
    {
        var excerpts = new List<DevelopmentCloudContextExcerpt>
        {
            new("src/Feature.cs", "sealed class Feature { }")
        };
        var builder = new DevelopmentCloudContextBuilder(new FixedTimeProvider(Now));

        var bundle = builder.Build(CreateBuildRequest(excerpts: excerpts));
        var originalHash = bundle.ContentHash;
        excerpts[0] = new DevelopmentCloudContextExcerpt("src/Feature.cs", "mutated");
        excerpts.Add(new DevelopmentCloudContextExcerpt("src/Extra.cs", "extra"));

        AssertEx.Equal(expected: 1, bundle.Excerpts.Count);
        AssertEx.Equal("sealed class Feature { }", bundle.ReadResource("excerpt:src/Feature.cs"));
        AssertEx.Equal(originalHash, bundle.ContentHash);
    }

    [Test]
    public async Task Build_WhenContextIsUnsafeOrUnbounded_FailsClosed()
    {
        var builder = new DevelopmentCloudContextBuilder(new FixedTimeProvider(Now), maximumBytes: 256, maximumEstimatedTokens: 256);

        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => Task.FromResult(builder.Build(
            CreateBuildRequest(excerpts: [new DevelopmentCloudContextExcerpt("../secret.txt", "content")]))));
        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => Task.FromResult(builder.Build(CreateBuildRequest(requirements: "password=not-a-real-secret"))));
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(builder.Build(CreateBuildRequest(requirements: new string('x', 512)))));
    }

    [Test]
    public void Create_WhenCloudRoleIsProjected_ExposesOnlyBundleReaderAndNoHistory()
    {
        var catalog = new DevelopmentCloudContextCatalog();
        var bundle = BuildBundle();
        var route = new DevelopmentCloudRoleRouteFactory(catalog).Create(bundle);

        AssertEx.Equal(expected: 2, route.Messages.Count);
        AssertEx.Equal(expected: 1, route.Options.Tools?.Count ?? 0);
        AssertEx.Equal("development_read_approved_context", route.Options.Tools![0].Name);
        AssertEx.False(route.Options.Tools.Any(tool => tool.Name.Contains("shell", StringComparison.OrdinalIgnoreCase)));
        AssertEx.False(route.Options.Tools.Any(tool => tool.Name.Contains("repository", StringComparison.OrdinalIgnoreCase)));
        AssertEx.True(catalog.TryGet(bundle.Id, out var registered));
        AssertEx.True(ReferenceEquals(bundle, registered));
    }

    [Test]
    [Arguments("local-only")]
    [Arguments("carrier")]
    [Arguments("version")]
    [Arguments("expired")]
    [Arguments("provider")]
    [Arguments("model")]
    [Arguments("bundle")]
    [Arguments("hash")]
    [Arguments("expiry")]
    [Arguments("ownership")]
    [Arguments("nonce")]
    public async Task Authorize_WhenAnyExactBindingDiffers_RejectsBeforeAudit(string mismatch)
    {
        var catalog = new DevelopmentCloudContextCatalog();
        var bundle = BuildBundle();
        catalog.Register(bundle);
        var audit = new CapturingAuditSink();
        var authorizer = new DevelopmentCloudEgressAuthorizer(catalog, audit, new FixedTimeProvider(Now));
        var request = CreateAuthorizationRequest(bundle, mismatch);

        await AssertEx.ThrowsAsync<CloudEgressAuthorizationException>(() => Task.Run(() => authorizer.Authorize(request)));

        AssertEx.Equal(expected: 0, audit.Records.Count);
    }

    [Test]
    public void Authorize_WhenAllBindingsMatch_AuditsMetadataOnly()
    {
        var catalog = new DevelopmentCloudContextCatalog();
        var bundle = BuildBundle();
        catalog.Register(bundle);
        var audit = new CapturingAuditSink();
        var authorizer = new DevelopmentCloudEgressAuthorizer(catalog, audit, new FixedTimeProvider(Now));

        authorizer.Authorize(CreateAuthorizationRequest(bundle));

        var record = AssertEx.NotNull(audit.Records.SingleOrDefault());
        AssertEx.Equal(bundle.ProjectId, record.ProjectId);
        AssertEx.Equal(bundle.ProviderName, record.ProviderName);
        AssertEx.Equal(bundle.ModelId, record.ModelId);
        AssertEx.Equal(bundle.ContentHash, record.BundleHash);
        AssertEx.False(record.ToString().Contains("sealed class Feature", StringComparison.Ordinal));
        AssertEx.False(record.ToString().Contains(bundle.Requirements, StringComparison.Ordinal));
    }

    [Test]
    public async Task Authorize_WhenApprovedBundleExceedsTransportLimit_FailsClosed()
    {
        var catalog = new DevelopmentCloudContextCatalog();
        var bundle = BuildBundle();
        catalog.Register(bundle);
        var audit = new CapturingAuditSink();
        var authorizer = new DevelopmentCloudEgressAuthorizer(catalog,
            audit,
            new FixedTimeProvider(Now),
            maximumBundleBytes: checked((int)bundle.ByteCount - 1));

        await AssertEx.ThrowsAsync<CloudEgressAuthorizationException>(() =>
            Task.Run(() => authorizer.Authorize(CreateAuthorizationRequest(bundle))));

        AssertEx.Equal(expected: 0, audit.Records.Count);
    }

    [Test]
    public async Task FunctionLoop_WhenCloudScoped_AuthorizesEveryRawRoundWithProductionAuthorizer()
    {
        var events = new List<string>();
        var catalog = new DevelopmentCloudContextCatalog();
        var bundle = BuildBundle();
        var route = new DevelopmentCloudRoleRouteFactory(catalog).Create(bundle);
        var audit = new CapturingAuditSink(events);
        using var localClient = new TwoRoundChatClient(events, "local");
        using var cloudClient = new TwoRoundChatClient(events, "cloud");
        var authorizer = new DevelopmentCloudEgressAuthorizer(catalog, audit, new FixedTimeProvider(Now));
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient, bundle.ProviderName), () => localClient, authorizer);
        using var functionLoop = runtime.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();

        _ = await functionLoop.GetResponseAsync(route.Messages, route.Options);

        AssertEx.Equal(expected: 2, cloudClient.TransportCount);
        AssertEx.Equal(expected: 0, localClient.TransportCount);
        AssertEx.Equal(expected: 2, audit.Records.Count);
        AssertEx.Equal("authorize1,transport1,authorize2,transport2", string.Join(',', events));
    }

    [Test]
    public async Task CoderModel_WhenCloudScoped_OffersOnlyBundleAndTypedSubmissionThenAppliesPatchLocally()
    {
        var route = new DevelopmentCloudRoleRouteFactory(new DevelopmentCloudContextCatalog()).Create(BuildBundle());
        using var chat = new RoleSubmittingChatClient(isReviewer: false);
        var cloud = new FixedCloudFactory(chat, route.ProviderName);
        var localResolver = Substitute.For<ILocalModelProviderResolver>();
        var workspace = new CapturingWorkspaceTools();
        var model = new DevelopmentCoderModel(chat, cloud, localResolver);

        var result = await model.RunAsync(route.ModelId,
            "must not be sent to cloud",
            workspace,
            maxOutputTokens: 64,
            maxToolCalls: 4,
            cloudRoute: route).ConfigureAwait(false);

        AssertEx.Equal("diff --git a/src/New.cs b/src/New.cs", workspace.AppliedPatch);
        AssertEx.Equal(expected: 2, chat.ToolNames.Count);
        AssertEx.True(chat.ToolNames.Contains("development_read_approved_context"));
        AssertEx.True(chat.ToolNames.Contains("submit_implementation"));
        AssertEx.False(chat.ToolNames.Any(name => name is "read_file" or "write_file" or "apply_patch" or "run_command"));
        AssertEx.Equal(expected: 0, result.Submission.CommandIds.Count);
        _ = localResolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReviewerModel_WhenCloudScoped_OffersNoWorkspaceOrMutationCapability()
    {
        var route = new DevelopmentCloudRoleRouteFactory(new DevelopmentCloudContextCatalog()).Create(BuildBundle());
        using var chat = new RoleSubmittingChatClient(isReviewer: true);
        var cloud = new FixedCloudFactory(chat, route.ProviderName);
        var localResolver = Substitute.For<ILocalModelProviderResolver>();
        var model = new DevelopmentReviewerModel(chat, cloud, localResolver);

        var result = await model.RunAsync(route.ModelId,
            "must not be sent to cloud",
            new CapturingWorkspaceTools(),
            maxOutputTokens: 64,
            maxToolCalls: 4,
            cloudRoute: route).ConfigureAwait(false);

        AssertEx.Equal(DevelopmentReviewDisposition.Approved, result.Submission.Disposition);
        AssertEx.Equal(expected: 2, chat.ToolNames.Count);
        AssertEx.True(chat.ToolNames.Contains("development_read_approved_context"));
        AssertEx.True(chat.ToolNames.Contains("submit_review"));
        AssertEx.False(chat.ToolNames.Any(name => name is "list_files" or "read_file" or "get_diff" or "write_file" or "apply_patch" or "run_command"));
        _ = localResolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AttemptContext_WhenCloudScoped_PersistsTheExactBundleBeforeCreatingRoute()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var snapshot = new DevelopmentExecutionSnapshot(projectId,
            taskId,
            attemptId,
            Guid.NewGuid(),
            "repository-hash",
            "main",
            DevelopmentEgressPolicy.CloudScoped,
            ConfigurationVersion: 1,
            TrustedRepositoryAcknowledged: true,
            TrustedRepositoryPolicyVersion: DevelopmentTrustPolicy.CurrentVersion,
            TrustedRepositoryAcknowledgedAtUtc: Now.ToUnixTimeMilliseconds(),
            MaxTokens: 64,
            MaxDurationSeconds: 60,
            "Bounded task",
            "Implement the bounded change",
            "[\"semantic tests pass\"]",
            DevelopmentTaskStatus.InProgress,
            TaskVersion: 2,
            DevelopmentAttemptRole.Coder,
            DevelopmentAttemptStatus.Running,
            "cloud-model",
            "fake-cloud",
            AttemptVersion: 1);
        var store = Substitute.For<IDevelopmentStore>();
        var blob = Substitute.For<IDevelopmentArtifactBlobStore>();
        blob.WriteAsync(projectId, Arg.Any<Guid>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => new DevelopmentArtifactBlobWriteResult("opaque/context", "BLOB-HASH", call.ArgAt<ReadOnlyMemory<byte>>(2).Length));
        var catalog = new DevelopmentCloudContextCatalog();
        var service = new DevelopmentCloudAttemptContextService(new DevelopmentCloudContextBuilder(new FixedTimeProvider(Now)),
            new DevelopmentCloudRoleRouteFactory(catalog),
            blob,
            store,
            Options.Create(new DevelopmentOptions
            {
                MaxAttemptDurationSeconds = 120
            }),
            new FixedTimeProvider(Now));

        var result = await service.CreateAsync(snapshot,
            [new DevelopmentCloudContextExcerpt("src/Feature.cs", "sealed class Feature { }")]).ConfigureAwait(false);

        AssertEx.Equal("fake-cloud", result.Route.ProviderName);
        AssertEx.Equal("cloud-model", result.Route.ModelId);
        _ = await store.Received(1).AttachArtifactAsync(Arg.Is<DevelopmentAttachArtifactCommand>(command =>
                command.ArtifactId == result.ArtifactId
                && command.ProjectId == projectId
                && command.TaskId == taskId
                && command.AttemptId == attemptId
                && command.Kind == DevelopmentArtifactKind.CloudContextBundle
                && command.ManagedReference == "opaque/context"),
            Arg.Any<CancellationToken>());
        AssertEx.Equal(expected: 1, result.Route.Options.Tools?.Count ?? 0);
    }

    private static DevelopmentCloudContextBundle BuildBundle() =>
        new DevelopmentCloudContextBuilder(new FixedTimeProvider(Now)).Build(CreateBuildRequest());

    private static DevelopmentCloudContextBuildRequest CreateBuildRequest(IReadOnlyList<DevelopmentCloudContextExcerpt>? excerpts = null,
        string requirements = "Implement the bounded change") =>
        new("bundle-1",
            "project-1",
            "task-1",
            "attempt-1",
            "fake-cloud",
            "cloud-model",
            requirements,
            "All semantic tests pass",
            "Use only approved context",
            excerpts ?? [new DevelopmentCloudContextExcerpt("src/Feature.cs", "sealed class Feature { }")],
            Now.AddMinutes(5),
            "nonce-1");

    private static CloudEgressAuthorizationRequest CreateAuthorizationRequest(DevelopmentCloudContextBundle bundle,
        string? mismatch = null)
    {
        var envelope = new DevelopmentCloudAuthorizationEnvelope(
            mismatch == "version" ? DevelopmentCloudAuthorizationEnvelope.CurrentVersion + 1 : DevelopmentCloudAuthorizationEnvelope.CurrentVersion,
            mismatch == "ownership" ? "other-project" : bundle.ProjectId,
            bundle.TaskId,
            bundle.AttemptId,
            mismatch == "local-only" ? DevelopmentExecutionPolicy.LocalOnly : DevelopmentExecutionPolicy.CloudScoped,
            mismatch == "bundle" ? "other-bundle" : bundle.Id,
            mismatch == "hash" ? "other-hash" : bundle.ContentHash,
            mismatch switch
            {
                "expiry" => bundle.ExpiresAt.AddSeconds(1),
                "expired" => Now.AddSeconds(-1),
                _ => bundle.ExpiresAt
            },
            mismatch == "nonce" ? "other-nonce" : bundle.Nonce);
        return new CloudEgressAuthorizationRequest(mismatch == "provider" ? "other-cloud" : bundle.ProviderName,
            mismatch == "model" ? "other-model" : bundle.ModelId,
            mismatch == "carrier" ? CloudEgressAuthorizationCarrierState.MalformedEnvelope : CloudEgressAuthorizationCarrierState.Valid,
            envelope);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            now;
    }

    private sealed class CapturingAuditSink(List<string>? events = null) : IDevelopmentCloudEgressAuditSink
    {
        public List<DevelopmentCloudEgressAudit> Records { get; } = [];

        public void Record(DevelopmentCloudEgressAudit audit)
        {
            Records.Add(audit);
            events?.Add($"authorize{Records.Count}");
        }
    }

    private sealed class FixedCloudFactory(IChatClient configuredClient, string providerName) : IActiveCloudChatClientFactory
    {
        public bool TryCreateActiveCloudChatClient(string? requestedModelId, out IChatClient? client)
        {
            client = configuredClient;
            return true;
        }

        public bool IsCloudProviderSelected(string? requestedModelId = null) =>
            true;

        public string? ResolveActiveCloudProviderName(string? requestedModelId = null) =>
            providerName;

        public void InvalidateSelectionCache()
        {
        }
    }

    private sealed class TwoRoundChatClient(List<string> events, string route) : IChatClient
    {
        public int TransportCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            TransportCount++;
            events.Add($"transport{TransportCount}");
            if (TransportCount == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "development_read_approved_context", new Dictionary<string, object?>
                    {
                        ["resource"] = "requirements"
                    })
                ])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"done-{route}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "unused");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RoleSubmittingChatClient(bool isReviewer) : IChatClient
    {
        public IReadOnlyList<string> ToolNames { get; private set; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ToolNames = options?.Tools?.Select(static tool => tool.Name).ToArray() ?? [];
            var toolName = isReviewer ? "submit_review" : "submit_implementation";
            var submit = AssertEx.NotNull(options?.Tools?.OfType<AIFunction>().SingleOrDefault(tool => tool.Name == toolName));
            if (isReviewer)
            {
                _ = await submit.InvokeAsync(new AIFunctionArguments
                {
                    ["disposition"] = "Approved",
                    ["summary"] = "Approved exact evidence.",
                    ["findings"] = Array.Empty<DevelopmentReviewFinding>()
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _ = await submit.InvokeAsync(new AIFunctionArguments
                {
                    ["summary"] = "Added the bounded file.",
                    ["patch"] = "diff --git a/src/New.cs b/src/New.cs",
                    ["changedFiles"] = new[]
                    {
                        "src/New.cs"
                    },
                    ["notes"] = null
                }, cancellationToken).ConfigureAwait(false);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 10,
                    TotalTokenCount = 20
                }
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            null;

        public void Dispose() { }
    }

    private sealed class CapturingWorkspaceTools : IDevelopmentWorkspaceTools
    {
        public string? AppliedPatch { get; private set; }
        public IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence => [];

        public Task<string> ListFilesAsync(string? path, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> SearchTextAsync(string pattern, string? path, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<string> ApplyPatchAsync(string patch, CancellationToken cancellationToken = default)
        {
            AppliedPatch = patch;
            return Task.FromResult("applied");
        }

        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> GetDiffAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> RunCommandAsync(string commandId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }
}
