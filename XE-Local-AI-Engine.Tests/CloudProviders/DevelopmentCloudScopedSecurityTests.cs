namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Runtime.CompilerServices;
using System.Text;
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
        using var runtime = new RuntimeChatClient(new FixedCloudFactory(cloudClient, bundle.ProviderName), () => localClient, authorizer, new FakeModelTrustResolver());
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
        var model = new DevelopmentCoderModel(chat, cloud, localResolver, new FakeModelTrustResolver(), NullLogger<DevelopmentCoderModel>.Instance);

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
        var model = new DevelopmentReviewerModel(chat, cloud, localResolver, new FakeModelTrustResolver(), NullLogger<DevelopmentReviewerModel>.Instance);

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
        var snapshot = CloudSnapshot(DevelopmentAttemptRole.Coder);
        var created = await CreateContextAsync(snapshot).ConfigureAwait(false);

        AssertEx.Equal("fake-cloud", created.Context.Route.ProviderName);
        AssertEx.Equal("cloud-model", created.Context.Route.ModelId);
        _ = await created.Store.Received(1).AttachArtifactAsync(Arg.Is<DevelopmentAttachArtifactCommand>(command =>
                command.ArtifactId == created.Context.ArtifactId
                && command.ProjectId == snapshot.ProjectId
                && command.TaskId == snapshot.TaskId
                && command.AttemptId == snapshot.AttemptId
                && command.Kind == DevelopmentArtifactKind.CloudContextBundle
                && command.ManagedReference == "opaque/context"),
            Arg.Any<CancellationToken>());
        AssertEx.Equal(expected: 1, created.Context.Route.Options.Tools?.Count ?? 0);
    }

    /// <summary>
    ///     A CloudScoped attempt is sent ONLY this bundle — the local prompt's policy section never leaves the node —
    ///     so the workflow's snapshotted rule text has to be in it. Until it was, a cloud-routed coder or reviewer ran
    ///     unconstrained while the task's <c>WorkflowPolicyApplied</c> event and its applied rule sets both claimed the
    ///     attempt had been governed by it. Asserted for both roles because one seam serves both runners.
    /// </summary>
    [Test]
    [Arguments(DevelopmentAttemptRole.Coder)]
    [Arguments(DevelopmentAttemptRole.Reviewer)]
    public async Task AttemptContext_WhenAWorkflowPolicyIsSnapshotted_CarriesItInTheProviderVisibleBundle(DevelopmentAttemptRole role)
    {
        var governed = await CreateContextAsync(CloudSnapshot(role, WorkflowPolicy)).ConfigureAwait(false);
        var ungoverned = await CreateContextAsync(CloudSnapshot(role)).ConfigureAwait(false);
        var governedBundle = AssertEx.NotNull(governed.Bundle);
        var ungovernedBundle = AssertEx.NotNull(ungoverned.Bundle);

        AssertEx.True(governedBundle.PolicyText.Contains(WorkflowPolicy, StringComparison.Ordinal),
            "The bundle a cloud role reads must carry the workflow's own policy text, not only the CloudScoped authorization sentence.");
        AssertEx.Equal(governedBundle.PolicyText, governedBundle.ReadResource("policy"));
        AssertEx.True(governedBundle.PolicyText.Contains("CloudScoped Development execution", StringComparison.Ordinal),
            "The CloudScoped authorization sentence must survive alongside the workflow's policy.");

        AssertEx.False(ungovernedBundle.PolicyText.Contains("House rules", StringComparison.Ordinal),
            "A task no workflow governs must carry no workflow policy.");
        AssertEx.True(ungovernedBundle.PolicyText.Contains("CloudScoped Development execution", StringComparison.Ordinal),
            "The CloudScoped authorization sentence is on every bundle.");

        // The egress authorizer binds the content hash, so this is what makes the policy tamper-evident rather than
        // decorative: the same attempt with and without it cannot present the same approved bundle.
        AssertEx.False(string.Equals(governedBundle.ContentHash, ungovernedBundle.ContentHash, StringComparison.Ordinal),
            "The bundle's content hash must cover the policy text.");
    }

    /// <summary>What a workflow renders onto the task: a heading its audit names and the body it snapshotted.</summary>
    private const string WorkflowPolicy = "## Policy: House rules\nNever touch production without an approved plan.";

    private static async Task<CreatedContext> CreateContextAsync(DevelopmentExecutionSnapshot snapshot)
    {
        var store = Substitute.For<IDevelopmentStore>();
        var blob = Substitute.For<IDevelopmentArtifactBlobStore>();
        blob.WriteAsync(snapshot.ProjectId, Arg.Any<Guid>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => new DevelopmentArtifactBlobWriteResult("opaque/context", "BLOB-HASH", call.ArgAt<ReadOnlyMemory<byte>>(2).Length));
        var builder = new CapturingContextBuilder(new DevelopmentCloudContextBuilder(new FixedTimeProvider(Now)));
        var service = new DevelopmentCloudAttemptContextService(builder,
            new DevelopmentCloudRoleRouteFactory(new DevelopmentCloudContextCatalog()),
            blob,
            store,
            Options.Create(new DevelopmentOptions
            {
                MaxAttemptDurationSeconds = 120
            }),
            new FixedTimeProvider(Now));

        var context = await service.CreateAsync(snapshot,
            [new DevelopmentCloudContextExcerpt("src/Feature.cs", "sealed class Feature { }")]).ConfigureAwait(false);
        return new CreatedContext(context, builder.Built, store);
    }

    private static DevelopmentExecutionSnapshot CloudSnapshot(DevelopmentAttemptRole role, string? workflowPolicyText = null) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
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
            role,
            DevelopmentAttemptStatus.Running,
            "cloud-model",
            "fake-cloud",
            AttemptVersion: 1,
            Encoding.UTF8.GetString(DevelopmentCommandProfileCatalog
                                    .Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null)
                                    .ToCanonicalUtf8()),
            PreviousRoundFeedback: null,
            workflowPolicyText);

    private sealed record CreatedContext(
        DevelopmentCloudAttemptContext Context,
        DevelopmentCloudContextBundle? Bundle,
        IDevelopmentStore Store);

    /// <summary>Hands back the REAL bundle the service built, which is the only thing a cloud role can read.</summary>
    private sealed class CapturingContextBuilder(IDevelopmentCloudContextBuilder inner) : IDevelopmentCloudContextBuilder
    {
        public DevelopmentCloudContextBundle? Built { get; private set; }

        public DevelopmentCloudContextBundle Build(DevelopmentCloudContextBuildRequest request) =>
            Built = inner.Build(request);
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

        /// <summary>
        ///     This stub only captures a patch; it never runs a catalog command, so the generic profile — the one
        ///     code-owned profile that needs no build target — is the honest stub value.
        /// </summary>
        public DevelopmentCommandProfile Profile { get; } =
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

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
