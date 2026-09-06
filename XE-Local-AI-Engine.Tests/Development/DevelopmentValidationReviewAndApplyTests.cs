namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevelopmentValidationReviewAndApplyTests : IDisposable
{
    private static readonly Guid SelectedFolderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    /// <summary>
    ///     L1 on the reviewer side: the round is budgeted against the window the model is REALLY serving, and reserves a
    ///     quarter of it rather than the whole configured output maximum. Same defect, same fix, separately proven —
    ///     the two roles open their own budget scopes and drifted apart once before.
    /// </summary>
    [Test]
    public async Task ReviewerModel_BudgetsTheRoundAgainstTheWindowTheRuntimeIsServing()
    {
        const int Served = 65_536;
        const int MaxOutput = 32_768;
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected(Arg.Any<string>()).Returns(false);
        using var chat = new CapturingReviewerChatClient();
        var model = new DevelopmentReviewerModel(chat,
            cloud,
            LocalModelResolver(Served, ReviewerLocalModel()),
            new FakeModelTrustResolver(),
            NullLogger<DevelopmentReviewerModel>.Instance);

        _ = await model.RunAsync("reviewer-local", "review", new NullWorkspaceTools(), MaxOutput, maxToolCalls: 8).ConfigureAwait(false);

        var options = AssertEx.NotNull(chat.Options);
        AssertEx.True(AssertEx.NotNull(options.AdditionalProperties).TryGetValue<int>(SamplingOptionKeys.NumCtx, out var numCtx),
            "the served window has to reach the request, because that key is what the provider-round budgeter prefers over its default.");
        AssertEx.Equal(Served, numCtx);

        var budget = AssertEx.NotNull(chat.Budget);
        AssertEx.Equal(Served, budget.DefaultContextTokens, "and the scope's own default names the same window, so the two cannot disagree.");
        AssertEx.Equal(Served / 4, budget.ReservedOutputTokenFloor, "a quarter of the window is reserved for the answer, not the whole configured maximum.");
        AssertEx.Equal<int?>(Served / 4, options.MaxOutputTokens);
    }

    /// <summary>
    ///     A runtime that reports no window keeps the conservative synthetic budget and says which one it fell back to,
    ///     sending no <c>num_ctx</c> override for a window nothing promised.
    /// </summary>
    [Test]
    public async Task ReviewerModel_WithNoServedWindow_FallsBackAndWarnsWhichFallbackItUsed()
    {
        const int MaxOutput = 4096;
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected(Arg.Any<string>()).Returns(false);
        using var chat = new CapturingReviewerChatClient();
        var logger = new RecordingLogger<DevelopmentReviewerModel>();
        var model = new DevelopmentReviewerModel(chat, cloud, LocalModelResolver(ReviewerLocalModel()), new FakeModelTrustResolver(), logger);

        _ = await model.RunAsync("reviewer-local", "review", new NullWorkspaceTools(), MaxOutput, maxToolCalls: 8).ConfigureAwait(false);

        var budget = AssertEx.NotNull(chat.Budget);
        AssertEx.Equal(MaxOutput * 2, budget.DefaultContextTokens, "the fallback is the pre-existing synthetic window, unchanged.");
        AssertEx.Equal(MaxOutput, budget.ReservedOutputTokenFloor, "and a fictional window does not also hand out a smaller reserve.");
        AssertEx.False(AssertEx.NotNull(chat.Options).AdditionalProperties?.ContainsKey(SamplingOptionKeys.NumCtx) ?? false,
            "a window nothing reported must not be asserted onto the request.");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "no served context window"),
            $"the operator has to be told which window the attempt was budgeted against: {string.Join(" | ", logger.Entries.Select(static entry => entry.Message))}");
    }

    /// <summary>
    ///     L3: a mis-spelled disposition is a correction, not the end of the attempt. Thrown, it terminalized the whole
    ///     reviewer round and cost the DevTask node one of its three attempts — three times in a row on 2026-09-02,
    ///     from a model that had approved the same subject correctly minutes earlier.
    /// </summary>
    [Test]
    public async Task ReviewerModel_WithAMisSpelledDisposition_CorrectsTheModelInsteadOfLosingTheAttempt()
    {
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected(Arg.Any<string>()).Returns(false);
        using var chat = new CorrectingReviewerChatClient();
        var model = new DevelopmentReviewerModel(chat,
            cloud,
            LocalModelResolver(ReviewerLocalModel()),
            new FakeModelTrustResolver(),
            NullLogger<DevelopmentReviewerModel>.Instance);

        var result = await model.RunAsync("reviewer-local", "review", new NullWorkspaceTools(), maxOutputTokens: 64, maxToolCalls: 8).ConfigureAwait(false);

        AssertEx.Equal(DevelopmentReviewDisposition.Approved, result.Submission.Disposition, "the corrected second call is the round's verdict.");
        AssertEx.Contains(AssertEx.NotNull(chat.FirstAnswer), "Approved", message: "the tool result names the two values that are accepted.");
        AssertEx.Contains(AssertEx.NotNull(chat.FirstAnswer), "ChangesRequested");
    }

    /// <summary>The reviewer's one local model, available and tool-capable, which is all these budget tests need.</summary>
    private static LocalModelDescriptor ReviewerLocalModel() =>
        new()
        {
            ModelName = "reviewer-local",
            ProviderName = "local",
            IsAvailable = true,
            SizeBytes = null,
            ModifiedAt = null,
            MaxContextTokens = 4096,
            IsToolCapable = true
        };

    /// <summary>
    ///     The validation list both code-owned .NET profiles carry, joined so order-sensitive comparison reads
    ///     clearly.
    /// </summary>
    private static readonly string ExpectedDotnetValidationProfile = string.Join(',',
        DevelopmentCommandIds.GitDiffCheck,
        DevelopmentCommandIds.DotnetRestore,
        DevelopmentCommandIds.DotnetBuildRelease,
        DevelopmentCommandIds.DotnetTestRelease);

    /// <summary>Reused because CA1869 forbids minting serializer options per operation.</summary>
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web);

    // Short prefix and truncated suffix on purpose: this fixture's root is the outermost of FOUR nested unique
    // segments, because DevelopmentWorkspaceProvider then appends development\workspaces\<projectId>\<taskId> as two
    // more full GUIDs. With the original 33-char prefix and full GUIDs the clone destination reached ~226 characters
    // under %TEMP%, and `git clone` died with "fatal: '$GIT_DIR' too big / fetch-pack: invalid index-pack output" once
    // index-pack appended its own \.git\objects\pack\tmp_idx_XXXXXX. core.longpaths does NOT lift that limit —
    // index-pack builds the temp-pack path into a fixed PATH_MAX buffer — so the only fix is a shorter path.
    // Production is unaffected: its data root is %LOCALAPPDATA%\XE-Local-AI-Engine, leaving ~90 characters of headroom.
    // 48 bits of randomness is ample for a per-run temporary directory.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-dev-rev-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>
    ///     Every sandbox this fixture's Development services asked for, in order. It exists so the egress posture a
    ///     full attempt actually RAN under can be asserted rather than inferred from the host's capabilities.
    /// </summary>
    private readonly List<SandboxCreateRequest> _sandboxCreates = [];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    [Test]
    public async Task LocalWorkflow_RequiresValidationIndependentReviewAndExplicitHashBoundApply()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("implemented\n"), new ApprovingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var coder = scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>();
        var validator = scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>();
        var reviewer = scope.ServiceProvider.GetRequiredService<IDevelopmentReviewerAttemptRunner>();
        var apply = scope.ServiceProvider.GetRequiredService<IDevelopmentApplyService>();
        var seed = Seed(repository);
        var repositoryBinding = Binding(seed, repository);
        scope.ServiceProvider.GetRequiredService<IDevelopmentRepositoryBindingService>()
             .ResolveProjectAsync(seed.ProjectId, Arg.Any<CancellationToken>())
             .Returns(repositoryBinding);

        _ = await coordinator.CreateProjectAsync(seed).ConfigureAwait(false);
        var ready = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                         Guid.NewGuid(),
                                         DevelopmentTaskStatus.Ready,
                                         ExpectedTaskVersion: 1))
                                     .ConfigureAwait(false);
        var coderAttemptId = Guid.NewGuid();
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 coderAttemptId,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Coder,
                                 "coder-local",
                                 "local",
                                 ready.Version))
                             .ConfigureAwait(false);
        _ = await coder.RunAsync(coderAttemptId, repositoryBinding).ConfigureAwait(false);

        var validation = await validator.RunAsync(seed.TaskId, repositoryBinding).ConfigureAwait(false);
        AssertEx.True(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, validation.TaskStatus);
        var inReview = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        var reviewerAttemptId = Guid.NewGuid();
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 reviewerAttemptId,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Reviewer,
                                 "reviewer-local",
                                 "local",
                                 inReview.Version))
                             .ConfigureAwait(false);
        var review = await reviewer.RunAsync(reviewerAttemptId, repositoryBinding).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentReviewDisposition.Approved, review.Disposition);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply, review.TaskStatus);

        var preview = await apply.PreviewAsync(seed.TaskId, repositoryBinding).ConfigureAwait(false);
        AssertEx.Equal(review.SubjectHash, preview.Subject.SubjectHash);
        AssertEx.Contains(preview.ChangedFiles.Select(static file => file.Path), "feature.txt");
        AssertEx.False(File.Exists(Path.Combine(repository, "feature.txt")));

        var operationId = Guid.NewGuid();
        var completed = await apply.ApplyAsync(seed.TaskId, operationId, repositoryBinding).ConfigureAwait(false);
        var replay = await apply.ApplyAsync(seed.TaskId, operationId, repositoryBinding).ConfigureAwait(false);
        AssertEx.Equal(completed, replay);
        AssertEx.Equal(DevelopmentOperationPhases.ApplyCompleted, completed.Phase);
        AssertEx.Equal("implemented\n", await File.ReadAllTextAsync(Path.Combine(repository, "feature.txt")).ConfigureAwait(false));
        AssertEx.Equal(DevelopmentTaskStatus.Completed, (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Status);
    }

    [Test]
    public async Task ValidationFailure_MovesTaskToChangesRequestedAndCannotStartReviewer()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("trailing whitespace \n"), new ApprovingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var coder = scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>();
        var validator = scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>();
        var seed = Seed(repository);
        var repositoryBinding = Binding(seed, repository);

        _ = await coordinator.CreateProjectAsync(seed).ConfigureAwait(false);
        var ready = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                         Guid.NewGuid(),
                                         DevelopmentTaskStatus.Ready,
                                         ExpectedTaskVersion: 1))
                                     .ConfigureAwait(false);
        var coderAttemptId = Guid.NewGuid();
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 coderAttemptId,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Coder,
                                 "coder-local",
                                 "local",
                                 ready.Version))
                             .ConfigureAwait(false);
        _ = await coder.RunAsync(coderAttemptId, repositoryBinding).ConfigureAwait(false);
        var validation = await validator.RunAsync(seed.TaskId, repositoryBinding).ConfigureAwait(false);

        AssertEx.False(validation.Passed);
        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested,
            task.Status,
            "a failed gate hands the failure to the coder; leaving it InProgress asked for the same validation again.");
        AssertEx.Equal(expected: 1, task.CurrentReviewRound, "and it spends a round, which is what bounds the rework loop.");
        AssertEx.Contains(AssertEx.NotNull(task.BlockedReason), "Deterministic validation failed", StringComparison.Ordinal);
        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                          Guid.NewGuid(),
                          Guid.NewGuid(),
                          DevelopmentAttemptRole.Reviewer,
                          "reviewer-local",
                          "local",
                          task.Version)))
                      .ConfigureAwait(false);
    }

    /// <summary>
    ///     Dependency-manifest rejection, asserted as the shape of the failure rather than only its wording. A change is a
    ///     verdict: the task moves to <c>ChangesRequested</c> carrying <c>dependency_manifest_changed</c>, the report
    ///     records it, and the attempt is NOT aborted as a security violation the way the test-write policy aborts.
    ///     The distinction is the point — "delete the failing test" is an attack, "add a package" is a legitimate task
    ///     this version cannot serve, and an agent can only retry usefully if it can tell which one it hit.
    /// </summary>
    [Test]
    public async Task Validation_WhenTheAttemptChangesADependencyManifest_ReturnsToChangesRequestedWithTheSpecificCode()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("<Project />\n", "Directory.Packages.props"),
                new ApprovingReviewerModel())
            .ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var seed = Seed(repository);
        var repositoryBinding = Binding(seed, repository);

        _ = await coordinator.CreateProjectAsync(seed).ConfigureAwait(false);
        var ready = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                         Guid.NewGuid(),
                                         DevelopmentTaskStatus.Ready,
                                         ExpectedTaskVersion: 1))
                                     .ConfigureAwait(false);
        var coderAttemptId = Guid.NewGuid();
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 coderAttemptId,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Coder,
                                 "coder-local",
                                 "local",
                                 ready.Version))
                             .ConfigureAwait(false);

        // The coder attempt itself succeeds: writing a manifest is not a security violation, so it produces evidence
        // and the gate is what refuses it.
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>()
                       .RunAsync(coderAttemptId, repositoryBinding)
                       .ConfigureAwait(false);

        var validation = await scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>()
                                    .RunAsync(seed.TaskId, repositoryBinding)
                                    .ConfigureAwait(false);

        AssertEx.False(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, validation.TaskStatus);
        var report = await ReadValidationReportAsync(scope.ServiceProvider, validation.ArtifactId, seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentValidationFailureCodes.DependencyManifestChanged, report.FailureCode);
        AssertEx.Contains(AssertEx.NotNull(report.FailureDetail), "Directory.Packages.props", StringComparison.Ordinal);

        // The gate deliberately ran nothing: the answer was known before the first command, and an attempt with no
        // egress cannot resolve the change anyway.
        AssertEx.Empty(report.Commands);
        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, task.Status);
    }

    [Test]
    public async Task Validation_WhenAnotherCoderAttemptIsRunning_RejectsWithoutChangingTaskState()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("implemented\n"), new ApprovingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = Seed(repository);
        var repositoryBinding = Binding(seed, repository);

        _ = await coordinator.CreateProjectAsync(seed).ConfigureAwait(false);
        var ready = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                         Guid.NewGuid(),
                                         DevelopmentTaskStatus.Ready,
                                         ExpectedTaskVersion: 1))
                                     .ConfigureAwait(false);
        var completedCoderAttemptId = Guid.NewGuid();
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 completedCoderAttemptId,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Coder,
                                 "coder-local",
                                 "local",
                                 ready.Version))
                             .ConfigureAwait(false);
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>()
                       .RunAsync(completedCoderAttemptId, repositoryBinding)
                       .ConfigureAwait(false);
        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 Guid.NewGuid(),
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Coder,
                                 "coder-local",
                                 "local",
                                 task.Version))
                             .ConfigureAwait(false);

        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>()
                                                                                     .RunAsync(seed.TaskId, repositoryBinding))
                      .ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Status);
    }

    [Test]
    public async Task ReviewerModel_OffersNoWritePatchCommandOrApplyCapability()
    {
        using var chat = new CapturingReviewerChatClient();
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected(Arg.Any<string>()).Returns(false);
        var model = new DevelopmentReviewerModel(chat,
            cloud,
            LocalModelResolver(new LocalModelDescriptor
            {
                ModelName = "reviewer-local",
                ProviderName = "local",
                IsAvailable = true,
                SizeBytes = null,
                ModifiedAt = null,
                MaxContextTokens = 4096,
                IsToolCapable = true
            }),
            new FakeModelTrustResolver(),
            NullLogger<DevelopmentReviewerModel>.Instance);

        var result = await model.RunAsync("reviewer-local",
            "review exact subject",
            new NullWorkspaceTools(),
            maxOutputTokens: 64,
            maxToolCalls: 8).ConfigureAwait(false);

        AssertEx.Equal(DevelopmentReviewDisposition.Approved, result.Submission.Disposition);
        AssertEx.False(chat.ToolNames.Contains("write_file"));
        AssertEx.False(chat.ToolNames.Contains("apply_patch"));
        AssertEx.False(chat.ToolNames.Contains("run_command"));
        AssertEx.False(chat.ToolNames.Contains("apply"));
    }

    [Test]
    public async Task RunAsync_WhenReviewRequestsChanges_ReturnsTaskToChangesRequestedAndBlocksApply()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("implemented\n"), new ChangesRequestingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();

        var (seed, reviewerAttemptId, review) = await RunThroughReviewAsync(scope.ServiceProvider, repository).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentReviewDisposition.ChangesRequested, review.Disposition);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, review.TaskStatus);
        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => scope.ServiceProvider.GetRequiredService<IDevelopmentApplyService>()
                                                                                     .PreviewAsync(seed.TaskId, Binding(seed, repository)))
                      .ConfigureAwait(false);

        // FU4-1, and asserted on the ChangesRequested path on purpose: the reviewer's prompt is written before the
        // model is called, so it is there whatever the model then decides. It is the only system record of what the
        // reviewer was told; every earlier claim about it was quoted back by the model itself.
        var prompts = (await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ListArtifactsAsync(seed.TaskId).ConfigureAwait(false))
                      .Where(static artifact => artifact.Kind == Client.Persistence.Entities.DevelopmentArtifactKind.Prompt)
                      .ToArray();
        var reviewerPrompt = AssertEx.NotNull(prompts.SingleOrDefault(artifact => artifact.AttemptId == reviewerAttemptId));
        AssertEx.Equal(review.SubjectHash, reviewerPrompt.SubjectHash);
        var payload = await scope.ServiceProvider.GetRequiredService<IDevelopmentArtifactBlobStore>()
                                 .ReadAsync(reviewerPrompt.ProjectId, reviewerPrompt.Id, reviewerPrompt.ContentHash, reviewerPrompt.ByteCount)
                                 .ConfigureAwait(false);
        AssertEx.Equal(DevelopmentArtifactReadStatus.Found, payload.Status);
        var promptText = Encoding.UTF8.GetString(payload.Content.Span);
        AssertEx.Contains(promptText, "Validated subject: " + review.SubjectHash);
        AssertEx.Contains(promptText, "Validation passed: True");
        AssertEx.Contains(promptText, "Use only the read-only tools.");

        // The coder's own round left one too, so the pair reads as the whole conversation the task was given. Asserted
        // as "some other attempt also has one" rather than as a count, so this breaks on a prompt-persistence
        // regression and not on a future change to the shared helper's round count.
        AssertEx.Contains(prompts, artifact => artifact.AttemptId != reviewerAttemptId);
    }

    [Test]
    public async Task PreviewAsync_WhenWorkspaceChangesAfterApproval_RejectsStaleEvidence()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("implemented\n"), new ApprovingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();

        var (seed, reviewerAttemptId, review) = await RunThroughReviewAsync(scope.ServiceProvider, repository).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentReviewDisposition.Approved, review.Disposition);
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var snapshot = await store.GetExecutionSnapshotAsync(reviewerAttemptId).ConfigureAwait(false);
        var session = await scope.ServiceProvider.GetRequiredService<IDevelopmentWorkspaceProvider>()
                                 .PrepareAsync(snapshot, Binding(seed, repository))
                                 .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(session.HostWorktreePath, "feature.txt"), "mutated after approval\n").ConfigureAwait(false);

        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => scope.ServiceProvider.GetRequiredService<IDevelopmentApplyService>()
                                                                                     .PreviewAsync(seed.TaskId, Binding(seed, repository)))
                      .ConfigureAwait(false);
        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, task.Status);
        AssertEx.Null(task.ApprovedSubjectHash);
        var approvalArtifacts = (await store.ListArtifactsAsync(seed.TaskId).ConfigureAwait(false))
                                .Where(artifact => artifact.Kind is Client.Persistence.Entities.DevelopmentArtifactKind.ValidationReport
                                    or Client.Persistence.Entities.DevelopmentArtifactKind.ReviewReport)
                                .ToArray();
        AssertEx.True(approvalArtifacts.Length >= 2);
        AssertEx.True(approvalArtifacts.All(static artifact => !artifact.IsValid));
        AssertEx.Contains(await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false),
            static developmentEvent => developmentEvent.EventType == "EvidenceInvalidated");
    }

    [Test]
    public async Task ReviewerRun_WhenSubmissionContainsCredential_RejectsBeforePersistingApproval()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("implemented\n"), new CredentialLeakingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();

        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => RunThroughReviewAsync(scope.ServiceProvider, repository))
                      .ConfigureAwait(false);
    }

    [Test]
    public async Task ArtifactSanitizer_RedactsProtectedPathsAndRejectsCredentialLikeContent()
    {
        var repository = Path.Combine(Path.GetTempPath(), "development-sanitizer-root");
        var sanitized = DevelopmentArtifactSanitizer.SanitizeText($"failure under {repository}/src/File.cs and /etc/passwd and C:\\Users\\operator\\secret.txt", repository);
        AssertEx.False(sanitized.Contains(repository, StringComparison.Ordinal));
        AssertEx.False(sanitized.Contains("/etc/passwd", StringComparison.Ordinal));
        AssertEx.False(sanitized.Contains("C:\\Users", StringComparison.Ordinal));
        AssertEx.Contains(sanitized, "[REDACTED:development-path]");
        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => Task.Run(() => DevelopmentArtifactSanitizer.SanitizeText("!Sensitive12345678")))
                      .ConfigureAwait(false);
    }

    /// <summary>
    ///     Command evidence is machine-generated build/test output, so it redacts rather than rejecting. Both inputs
    ///     here are shapes that ordinary `dotnet build` / `dotnet test` output really produces: an absolute output
    ///     path, and a long descriptive test-method name. Each on its own used to clear the secret scanner's
    ///     keyword-free entropy fallback and make the whole validation report unpersistable — which meant the gate
    ///     could neither pass nor cleanly fail on this repository once the real command profile was enabled.
    /// </summary>
    [Test]
    public async Task ArtifactSanitizer_ForCommandEvidence_RedactsInsteadOfRejectingOrdinaryBuildOutput()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var evidence = new DevelopmentCommandEvidence(DevelopmentCommandIds.DotnetTestRelease,
            ExitCode: 1,
            Completed: true,
            OutputTruncated: false,
            DurationMilliseconds: 1234,
            "  /home/operator/projects/engine/tests/Probe/bin/Release/net10\n"
            + "failed ApplyThinkingSwitch_MarkerAbsent_BodyHasNoChatTemplateKwargs (12ms)\n",
            "AccountKey=abcdefghijklmnopqrstuvwxyz012345\n");

        // The point of the test: this returns evidence at all. Before, it threw and the whole validation report was
        // unpersistable, so the run had no outcome — neither a pass nor an honest failure.
        var sanitized = DevelopmentArtifactSanitizer.Sanitize(evidence, "/home/operator/projects/engine");

        // The exit code and the surrounding structure survive, which is what the gate's verdict rests on.
        AssertEx.Equal(expected: 1, sanitized.ExitCode);
        AssertEx.Contains(sanitized.StandardOutput, "[REDACTED:development-path]");
        AssertEx.Contains(sanitized.StandardOutput, "failed ");
        AssertEx.Contains(sanitized.StandardOutput, "(12ms)");

        // Known cost of the redact-don't-reject policy, pinned deliberately rather than left to be rediscovered:
        // the failing test's NAME is itself high-entropy enough to be redacted, so the operator sees that a test
        // failed but not which one. That is a diagnostic loss; it is not a correctness loss, and it is strictly
        // better than the whole report being rejected. The shared scanner currently applies this broad fallback to
        // ordinary identifiers with the same shape.
        AssertEx.Contains(sanitized.StandardOutput, "[REDACTED:high-entropy-token]");

        // A genuine credential is still removed — redacted rather than leaked.
        AssertEx.Contains(sanitized.StandardError, "[REDACTED:");
        AssertEx.False(sanitized.StandardError.Contains("abcdefghijklmnopqrstuvwxyz012345", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The structurally unredactable cases still reject the artifact outright, for command evidence too.
    /// </summary>
    [Test]
    public async Task ArtifactSanitizer_ForCommandEvidence_StillRejectsAPrivateKeyBlock()
    {
        var evidence = new DevelopmentCommandEvidence(DevelopmentCommandIds.DotnetBuildRelease,
            ExitCode: 0,
            Completed: true,
            OutputTruncated: false,
            DurationMilliseconds: 10,
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEow==\n-----END RSA PRIVATE KEY-----\n",
            string.Empty);

        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => Task.Run(() => DevelopmentArtifactSanitizer.Sanitize(evidence)))
                      .ConfigureAwait(false);
    }

    [Test]
    public async Task TransitionTaskAsync_WhenFourthReviewRoundWouldStart_Rejects()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("implemented\n"), new ApprovingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = Seed(repository);

        _ = await coordinator.CreateProjectAsync(seed).ConfigureAwait(false);
        var current = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                           Guid.NewGuid(),
                                           DevelopmentTaskStatus.Ready,
                                           ExpectedTaskVersion: 1))
                                       .ConfigureAwait(false);
        current = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                       Guid.NewGuid(),
                                       DevelopmentTaskStatus.InProgress,
                                       current.Version))
                                   .ConfigureAwait(false);
        for (var round = 0; round < 3; round++)
        {
            current = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                           Guid.NewGuid(),
                                           DevelopmentTaskStatus.Validation,
                                           current.Version))
                                       .ConfigureAwait(false);
            current = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                           Guid.NewGuid(),
                                           DevelopmentTaskStatus.InReview,
                                           current.Version))
                                       .ConfigureAwait(false);
            current = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                           Guid.NewGuid(),
                                           DevelopmentTaskStatus.ChangesRequested,
                                           current.Version))
                                       .ConfigureAwait(false);
            current = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                           Guid.NewGuid(),
                                           DevelopmentTaskStatus.InProgress,
                                           current.Version))
                                       .ConfigureAwait(false);
        }

        current = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                       Guid.NewGuid(),
                                       DevelopmentTaskStatus.Validation,
                                       current.Version))
                                   .ConfigureAwait(false);
        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                          Guid.NewGuid(),
                          DevelopmentTaskStatus.InReview,
                          current.Version)))
                      .ConfigureAwait(false);
        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.Validation, task.Status);
        AssertEx.Equal(expected: 3, task.CurrentReviewRound);
    }

    /// <summary>
    ///     The reviewer's half of the whole-attempt output budget. See
    ///     <c>DevelopmentWorkspaceAndCoderTests.CoderModel_AcceptsCumulativeOutputAcrossRoundsAndRejectsOnlyAboveTheWholeAttemptCeiling</c>
    ///     for why the old <c>maxOutputTokens + 1</c> expectation pinned a defect rather than a rule: the cap is
    ///     per provider call, the usage report is cumulative over the tool loop.
    /// </summary>
    [Test]
    public async Task ReviewerModel_AcceptsCumulativeOutputAcrossRoundsAndRejectsOnlyAboveTheWholeAttemptCeiling()
    {
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected(Arg.Any<string>()).Returns(false);
        var resolver = LocalModelResolver(new LocalModelDescriptor
        {
            ModelName = "reviewer-local",
            ProviderName = "local",
            IsAvailable = true,
            SizeBytes = null,
            ModifiedAt = null,
            MaxContextTokens = 65_536,
            IsToolCapable = true
        });
        using var exactChat = new CapturingReviewerChatClient(inputTokens: 40_000, outputTokens: 64);
        var exact = new DevelopmentReviewerModel(exactChat,
            cloud,
            resolver, new FakeModelTrustResolver(), NullLogger<DevelopmentReviewerModel>.Instance);

        var result = await exact.RunAsync("reviewer-local",
            "review exact subject",
            new NullWorkspaceTools(),
            maxOutputTokens: 64,
            maxToolCalls: 8).ConfigureAwait(false);

        AssertEx.Equal<long?>(40_000, result.InputTokens);
        AssertEx.Equal<long?>(64, result.OutputTokens);

        // More than one call's budget across the loop is normal, and must be accepted.
        using var multiRoundChat = new CapturingReviewerChatClient(inputTokens: 40_000, outputTokens: 65);
        var multiRound = new DevelopmentReviewerModel(multiRoundChat, cloud, resolver, new FakeModelTrustResolver(), NullLogger<DevelopmentReviewerModel>.Instance);
        var accepted = await multiRound.RunAsync("reviewer-local",
                                           "review exact subject",
                                           new NullWorkspaceTools(),
                                           maxOutputTokens: 64,
                                           maxToolCalls: 8)
                                       .ConfigureAwait(false);
        AssertEx.Equal<long?>(65, accepted.OutputTokens);

        // maxToolCalls 8 => at most 9 provider calls => a whole-attempt ceiling of 9 x 64.
        const int Ceiling = 9 * 64;
        using var overChat = new CapturingReviewerChatClient(inputTokens: 40_000, outputTokens: Ceiling + 1);
        var over = new DevelopmentReviewerModel(overChat, cloud, resolver, new FakeModelTrustResolver(), NullLogger<DevelopmentReviewerModel>.Instance);
        var failure = await AssertEx.ThrowsAsync<DevelopmentAttemptEvidenceException>(() => over.RunAsync("reviewer-local",
                                        "review exact subject",
                                        new NullWorkspaceTools(),
                                        maxOutputTokens: 64,
                                        maxToolCalls: 8))
                                    .ConfigureAwait(false);
        AssertEx.Equal(DevelopmentAttemptFailureCodes.OutputTokenBudgetExceeded, failure.FailureCode);
    }

    /// <summary>
    ///     The validation list is no longer a <c>DevelopmentOptions</c> setting — it is per-profile and code-owned, so
    ///     what the gate runs on any install is whatever <c>DevelopmentCommandProfileCatalog</c> materializes. This
    ///     pins that content, per profile, and it is the regression guard for the false-green defect fixed in
    ///     <c>18fa1d3e</c>: before that fix the shipped list was <c>git_diff_check</c> alone, so a patch that did not
    ///     compile — or compiled and failed its tests — reached <c>InReview</c> and could be applied.
    ///     <para>
    ///         Shrinking either dotnet profile's list re-opens exactly that defect. Dropping
    ///         <c>dotnet_test_release_no_build</c> makes a failing test invisible; dropping
    ///         <c>dotnet_build_release_no_restore</c> makes a broken build invisible; reordering them makes the gate
    ///         fail for the wrong reason, because each command depends on the one before it. <c>generic-git</c> is the
    ///         deliberate exception and is asserted to be exactly the whitespace check: it is the honest answer for a
    ///         repository with no detected .NET build system, and it is surfaced to the operator at confirmation
    ///         rather than applied silently.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CodeOwnedProfiles_ValidateWhitespaceRestoreBuildAndTestInDependencyOrder()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        AssertEx.Equal(ExpectedDotnetValidationProfile,
            string.Join(',',
                DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "Fixture.slnx")
                                                .ValidationCommandIds));
        AssertEx.Equal(ExpectedDotnetValidationProfile,
            string.Join(',',
                DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetCsproj, "src/Lib/Lib.csproj")
                                                .ValidationCommandIds));
        AssertEx.Equal(DevelopmentCommandIds.GitDiffCheck,
            string.Join(',',
                DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null)
                                                .ValidationCommandIds));
    }

    [Test]
    public async Task Validation_UnderTheDotnetSlnxProfile_WhenPatchCompilesAndPasses_ReachesInReviewWithEveryCommandRecorded()
    {
        var (validation, task, commands, report) = await RunDotnetProfileValidationAsync(SlnxProfile(), DevelopmentSyntheticSolutionRepository.PassingLibrarySource)
            .ConfigureAwait(false);

        AssertEx.True(validation.Passed, DescribeValidation(commands, report));
        AssertEx.Equal(DevelopmentTaskStatus.InReview, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, task.Status);
        AssertEx.Equal(ExpectedDotnetValidationProfile, string.Join(',', commands.Select(static command => command.CommandId)));
        AssertEx.True(commands.All(static command => command.Completed && command.ExitCode == 0), DescribeValidation(commands, report));

        // The gate no longer just knows the test command exited zero, it knows what the suite did. The
        // fixture has exactly one test, and it ran.
        var outcome = TestOutcome(commands);
        AssertEx.True(outcome.Parsed);
        AssertEx.Equal(expected: 1, outcome.Discovered);
        AssertEx.Equal(expected: 1, outcome.Executed);
        AssertEx.Equal(expected: 1, outcome.Passed);

        // The egress-policy acceptance criterion is asserted on what the run actually asked for rather than on what this host can
        // do. A full attempt against the synthetic solution — coder, then the whole validation gate — completed green
        // while every agent-facing sandbox it created carried the posture this backend can actually serve, and the
        // only sandbox that had egress by design was the short-lived warm restore. The agent-facing posture is
        // capability-gated (Option B), so it is computed from the same backend the fixture wired rather than assumed.
        AssertEx.NotEmpty(_sandboxCreates.Where(static request => request.RuntimeProfile == "development-warm"));
        AssertEx.Empty(_sandboxCreates.Where(static request => request.RuntimeProfile == "development-warm"
                                                               && request.NetworkPolicy != SandboxNetworkPolicy.Unrestricted));

        using var backend = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
        var expectedAgentFacingPolicy = backend.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy)
            ? SandboxNetworkPolicy.None
            : SandboxNetworkPolicy.Unrestricted;

        var agentFacing = _sandboxCreates.Where(static request => request.RuntimeProfile == "development-local").ToArray();
        AssertEx.NotEmpty(agentFacing);
        AssertEx.Empty(agentFacing.Where(request => request.NetworkPolicy != expectedAgentFacingPolicy),
            "the backend's advertised SupportsNetworkPolicy decides the posture: None where it can deny egress, Unrestricted where it cannot (Option B).");
        AssertEx.Equal(expected: 0, outcome.Failed);

        // Only the test command carries a result. A build or a whitespace check has none, and must not be given an
        // empty one that the executed>0 rule would then reject.
        AssertEx.True(commands.Where(static command => !string.Equals(command.CommandId, DevelopmentCommandIds.DotnetTestRelease, StringComparison.Ordinal))
                              .All(static command => command.TestOutcome is null));

        // A passing report carries no failure code, so a UI can key on its presence alone.
        AssertEx.Null(report.FailureCode);
        AssertEx.Null(report.FailureDetail);
    }

    /// <summary>
    ///     The first of the two outcomes the former one-command profile silently accepted: a patch that does not
    ///     compile. Under that profile this reached <c>InReview</c> and could be applied to the trusted repository.
    /// </summary>
    [Test]
    public async Task Validation_UnderTheDotnetSlnxProfile_WhenPatchBreaksTheBuild_DoesNotReachInReview()
    {
        var (validation, task, commands, report) = await RunDotnetProfileValidationAsync(SlnxProfile(), DevelopmentSyntheticSolutionRepository.BuildBreakingLibrarySource)
            .ConfigureAwait(false);

        AssertEx.False(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, task.Status);

        // The build is what rejected it: the whitespace check and the restore ahead of it both succeeded.
        AssertCommandSucceeded(commands, DevelopmentCommandIds.GitDiffCheck);
        AssertCommandSucceeded(commands, DevelopmentCommandIds.DotnetRestore);
        AssertEx.NotEqual(notExpected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetBuildRelease));

        // The runner does NOT stop at the first failure — it runs the whole declared profile. (An earlier revision of
        // this test said otherwise in a comment; the loop in DevelopmentValidationRunner has no break.) So the test
        // command still runs, and what it does then is worth pinning: measured, `dotnet test --no-build` finds no
        // test binary to launch and reports a perfectly READABLE "Zero tests ran" summary with all-zero counts. This
        // is the shape the executed>0 rule exists for — the counts parse cleanly and they are zero.
        var outcome = TestOutcome(commands);
        AssertEx.True(outcome.Parsed);
        AssertEx.Equal(expected: 0, outcome.Executed);

        // The reported reason is still the build, because the verdict takes the first failing command in profile
        // order rather than the last. An operator reading this gets "the build broke", not "no tests ran".
        AssertEx.Equal(DevelopmentValidationFailureCodes.CommandFailed, report.FailureCode);
        AssertEx.Contains(report.FailureDetail, DevelopmentCommandIds.DotnetBuildRelease);
    }

    /// <summary>
    ///     The second outcome the former one-command profile silently accepted: a patch that compiles cleanly and
    ///     fails its test. This one is invisible to every check that profile performed.
    /// </summary>
    [Test]
    public async Task Validation_UnderTheDotnetSlnxProfile_WhenPatchFailsATest_DoesNotReachInReview()
    {
        var (validation, task, commands, report) = await RunDotnetProfileValidationAsync(SlnxProfile(), DevelopmentSyntheticSolutionRepository.TestFailingLibrarySource)
            .ConfigureAwait(false);

        AssertEx.False(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, task.Status);

        // The change compiles — only the test rejected it, which is the signal the old default could not see at all.
        AssertCommandSucceeded(commands, DevelopmentCommandIds.GitDiffCheck);
        AssertCommandSucceeded(commands, DevelopmentCommandIds.DotnetRestore);
        AssertCommandSucceeded(commands, DevelopmentCommandIds.DotnetBuildRelease);
        AssertEx.NotEqual(notExpected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetTestRelease));

        // The report now says WHAT failed, not merely that something did. One test ran and it failed.
        var outcome = TestOutcome(commands);
        AssertEx.True(outcome.Parsed);
        AssertEx.Equal(expected: 1, outcome.Executed);
        AssertEx.Equal(expected: 0, outcome.Passed);
        AssertEx.Equal(expected: 1, outcome.Failed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.TestsFailed, report.FailureCode);
    }

    /// <summary>
    ///     The policy for a registered repository that has no tests at all.
    ///     <para>
    ///         Its build target compiles perfectly well; there is simply nothing to run. Before this policy existed, the gate had
    ///         no opinion about that beyond the test command's exit code, and a runner that answered zero would have
    ///         been accepted as a pass. The rule now is that a change cannot be validated by a suite that ran nothing,
    ///         and — because this is a state the operator can actually fix — it is reported with its own code rather
    ///         than collapsed into "validation failed".
    ///     </para>
    ///     <para>
    ///         The test-write policy is what makes the failure recoverable rather than a dead end: the agent may ADD
    ///         tests, it just may
    ///         not weaken or delete the ones that existed at <c>BaseCommit</c>. So the remedy for this failure is
    ///         available to the same loop that hit it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Validation_WhenTheRegisteredRepositoryHasNoTests_FailsWithItsOwnReasonRatherThanPassing()
    {
        var (validation, task, commands, report) = await RunDotnetProfileValidationAsync(SlnxProfile(),
                DevelopmentSyntheticSolutionRepository.PassingLibrarySource,
                includeTests: false)
            .ConfigureAwait(false);

        AssertEx.False(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, task.Status);

        // Everything up to the test command is genuinely green — this is not a build failure wearing a different hat.
        AssertCommandSucceeded(commands, DevelopmentCommandIds.GitDiffCheck);
        AssertCommandSucceeded(commands, DevelopmentCommandIds.DotnetRestore);
        AssertCommandSucceeded(commands, DevelopmentCommandIds.DotnetBuildRelease);

        var outcome = TestOutcome(commands);
        AssertEx.False(outcome.Parsed);
        AssertEx.Equal(DevelopmentTestParseFailureCodes.NoTestProjects, outcome.ParseFailureCode);
        AssertEx.Equal(DevelopmentValidationFailureCodes.TestResultsUnparsed, report.FailureCode);
    }

    /// <summary>
    ///     The second code-owned .NET profile, driven end to end against the fixture's test project. The three tests
    ///     above only ever prove <c>dotnet-slnx</c> works; a profile that is offered to operators but never executed
    ///     in a test is a profile nobody has run.
    ///     <para>
    ///         <c>tests/Probe/Probe.csproj</c> is a real target for this profile rather than a stand-in: it is an
    ///         <c>OutputType=Exe</c> TUnit project with a <c>ProjectReference</c> to the library the coder rewrites,
    ///         so <c>dotnet restore/build/test</c> against it compiles and exercises the same change the solution
    ///         profile does — through a different build target and therefore a different argument vector and digest.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Validation_UnderTheDotnetCsprojProfile_WhenPatchCompilesAndPasses_ReachesInReviewWithEveryCommandRecorded()
    {
        var (validation, task, commands, report) = await RunDotnetProfileValidationAsync(CsprojProfile(), DevelopmentSyntheticSolutionRepository.PassingLibrarySource)
            .ConfigureAwait(false);

        AssertEx.True(validation.Passed, DescribeValidation(commands, report));
        AssertEx.Equal(DevelopmentTaskStatus.InReview, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, task.Status);
        AssertEx.Equal(ExpectedDotnetValidationProfile, string.Join(',', commands.Select(static command => command.CommandId)));
        AssertEx.True(commands.All(static command => command.Completed && command.ExitCode == 0), DescribeValidation(commands, report));

        // The adapter is bound to the profile FAMILY, not to one profile, so the second .NET profile must read its
        // results too — otherwise dotnet-csproj would silently run without the executed>0 rule applying to it.
        var outcome = TestOutcome(commands);
        AssertEx.True(outcome.Parsed);
        AssertEx.Equal(expected: 1, outcome.Executed);
        AssertEx.Equal(expected: 1, outcome.Passed);
        AssertEx.Null(report.FailureCode);

        // The two .NET profiles must not be interchangeable at the digest level even on the same repository: they
        // run different argument vectors, and an artifact stamped with one must never verify against the other.
        AssertEx.NotEqual(SlnxProfile().ComputeDigest(), CsprojProfile().ComputeDigest());
    }

    /// <summary>
    ///     The failing half, kept to the test-failing source rather than the build-breaking one because it is the
    ///     stronger evidence: reaching a failed <c>dotnet_test_release_no_build</c> proves all four commands really
    ///     executed under this profile, where a build break would stop one command earlier.
    /// </summary>
    [Test]
    public async Task Validation_UnderTheDotnetCsprojProfile_WhenPatchFailsATest_DoesNotReachInReview()
    {
        var (validation, task, commands, report) = await RunDotnetProfileValidationAsync(CsprojProfile(), DevelopmentSyntheticSolutionRepository.TestFailingLibrarySource)
            .ConfigureAwait(false);

        AssertEx.False(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, task.Status);

        AssertCommandSucceeded(commands, DevelopmentCommandIds.GitDiffCheck);
        AssertCommandSucceeded(commands, DevelopmentCommandIds.DotnetRestore);
        AssertCommandSucceeded(commands, DevelopmentCommandIds.DotnetBuildRelease);
        AssertEx.NotEqual(notExpected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetTestRelease));

        AssertEx.Equal(expected: 1, TestOutcome(commands).Failed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.TestsFailed, report.FailureCode);
    }

    /// <summary>
    ///     THE LIVELOCK PIN. A task whose deterministic validation keeps failing must keep asking the CODER for rounds
    ///     until its review budget runs out, and must never be handed the same validation twice for one coder attempt.
    ///     <para>
    ///         Before the failed gate routed to <c>ChangesRequested</c>, it returned the task to <c>InProgress</c> —
    ///         which, behind a succeeded coder attempt, is exactly the state <c>StartNextActionAsync</c> reads as
    ///         "implemented, validate it". Every tick re-ran the whole command profile against the same patch.
    ///         Measured live on 2026-09-04: 289 validation runs in 25 minutes, 282 report rows on one task, zero coder
    ///         rounds, ended only by cancelling the run. The loop below is bounded so that failure mode exhausts the
    ///         ticks and fails the assertion instead of hanging.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AFailingGateSpendsCoderRoundsUntilTheBudgetIsGone_AndNeverValidatesOneAttemptTwice()
    {
        const int TickCeiling = 24;
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("trailing whitespace \n"), new ApprovingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coder = scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>();
        var validator = scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>();
        var seed = Seed(repository);
        var repositoryBinding = Binding(seed, repository);
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>().CreateProjectAsync(seed).ConfigureAwait(false);
        var service = CreateManagementService(scope.ServiceProvider);

        var actions = new List<string>();
        var validatedAttempts = new List<Guid>();
        var coderRounds = 0;
        for (var tick = 0; tick < TickCeiling; tick++)
        {
            var action = await service.StartNextActionAsync(seed.ProjectId, seed.TaskId, Guid.NewGuid()).ConfigureAwait(false);
            actions.Add(action.Action);
            if (action.Action == "Blocked")
            {
                break;
            }

            if (action.Action == "Validation")
            {
                // WHICH attempt this validation is about, read before it runs: two entries for one id is the livelock.
                validatedAttempts.Add((await store.ListAttemptsAsync(seed.TaskId).ConfigureAwait(false))
                                      .Last(attempt => attempt.Role == DevelopmentAttemptRole.Coder && attempt.Status == Client.Persistence.Entities.DevelopmentAttemptStatus.Succeeded)
                                      .Id);
                _ = await validator.RunAsync(seed.TaskId, repositoryBinding).ConfigureAwait(false);
                continue;
            }

            coderRounds++;
            _ = await coder.RunAsync(action.AttemptId!.Value, repositoryBinding).ConfigureAwait(false);
        }

        var trail = string.Join(", ", actions);
        AssertEx.Contains(actions, static action => action == "Blocked", $"the loop has to END, and on the budget rather than on the tick ceiling: {trail}");
        AssertEx.Equal(validatedAttempts.Count,
            validatedAttempts.Distinct().Count(),
            $"no coder attempt may be validated twice — that repetition IS the livelock: {trail}");

        // Every validation is preceded by the coder round it judges, so "Validation" never follows "Validation".
        AssertEx.Empty(actions.Zip(actions.Skip(1)).Where(static pair => pair is { First: "Validation", Second: "Validation" }),
            $"a failed gate hands the round to the coder; asking for validation again is the state the fix removes: {trail}");

        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.Blocked, task.Status, "and it ends where a rejected FINAL review round ends, by the same route.");
        AssertEx.Contains(AssertEx.NotNull(task.BlockedReason), "maximum number of rounds");
        AssertEx.Equal(task.MaxReviewRounds, task.CurrentReviewRound, "the failing gate spent the rounds, which is what bounded the loop.");
        AssertEx.Equal(task.MaxReviewRounds, validatedAttempts.Count, "one validation per round, no more.");
        AssertEx.Equal(task.MaxReviewRounds,
            coderRounds,
            "and one CODER round per round: the budget check runs before the rework attempt, so the task is stood down "
            + "the moment the budget is gone rather than after a whole model attempt that could never reach a review.");
    }

    /// <summary>
    ///     The coder round a failed gate asks for is told what the gate found, or it re-implements blind. Same route a
    ///     reviewer's rejection travels — the task's own event log — which is why the failed hop has to leave the task
    ///     somewhere a coder round starts from.
    /// </summary>
    [Test]
    public async Task TheRoundAFailedGateAsksForIsBriefedWithTheGatesOwnComplaint()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("trailing whitespace \n"), new ApprovingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = Seed(repository);
        var repositoryBinding = Binding(seed, repository);
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>().CreateProjectAsync(seed).ConfigureAwait(false);
        var service = CreateManagementService(scope.ServiceProvider);

        var first = await service.StartNextActionAsync(seed.ProjectId, seed.TaskId, Guid.NewGuid()).ConfigureAwait(false);
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>()
                       .RunAsync(first.AttemptId!.Value, repositoryBinding)
                       .ConfigureAwait(false);
        AssertEx.Equal("Validation", (await service.StartNextActionAsync(seed.ProjectId, seed.TaskId, Guid.NewGuid()).ConfigureAwait(false)).Action);
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>().RunAsync(seed.TaskId, repositoryBinding).ConfigureAwait(false);

        var rework = await service.StartNextActionAsync(seed.ProjectId, seed.TaskId, Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Equal("Attempt", rework.Action);
        AssertEx.Equal(DevelopmentAttemptRole.Coder, rework.Role, "the failure is the coder's to fix, not something to re-measure.");
        var feedback = AssertEx.NotNull((await store.GetExecutionSnapshotAsync(rework.AttemptId!.Value).ConfigureAwait(false)).PreviousRoundFeedback,
            "the round must be told what the gate found.");
        AssertEx.Contains(feedback, "Deterministic validation failed", StringComparison.Ordinal);
        AssertEx.Contains(feedback, DevelopmentValidationFailureCodes.CommandFailed, StringComparison.Ordinal);
    }

    /// <summary>
    ///     THE EXCEPTION-PATH PIN. A deterministic validation gets ONE automatic re-run, then a human.
    ///     <para>
    ///         The gate's <c>catch</c> returns the task to <c>InProgress</c> behind a succeeded coder attempt, which is
    ///         the state the next-action decision reads as "implemented, validate it" — so before the recovery was
    ///         counted, a validation that threw deterministically re-ran for as long as anything kept asking, running
    ///         the whole command profile each time. Same symptom as the failed-gate livelock, different door.
    ///     </para>
    ///     <para>
    ///         The throw is real rather than injected: a repository binding whose identity hash does not match the
    ///         persisted one is refused by <c>DevelopmentWorkspaceProvider.PrepareAsync</c> every single time, which is
    ///         exactly the deterministic fault the bound exists for. The loop is bounded so the old behaviour exhausts
    ///         the ticks and fails the count instead of hanging.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AValidationThatAlwaysThrowsRecoversOnceAndThenStandsTheTaskDown()
    {
        const int TickCeiling = 10;
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("implemented\n"), new ApprovingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coder = scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>();
        var validator = scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>();
        var seed = Seed(repository);
        var repositoryBinding = Binding(seed, repository);
        var refusedBinding = repositoryBinding with
        {
            RepositoryIdentityHash = "not-the-persisted-repository-identity"
        };
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>().CreateProjectAsync(seed).ConfigureAwait(false);
        var service = CreateManagementService(scope.ServiceProvider);

        var actions = new List<string>();
        var statusAfterEachValidation = new List<DevelopmentTaskStatus>();
        for (var tick = 0; tick < TickCeiling; tick++)
        {
            DevelopmentNextActionResult action;
            try
            {
                action = await service.StartNextActionAsync(seed.ProjectId, seed.TaskId, Guid.NewGuid()).ConfigureAwait(false);
            }
            catch (DevelopmentInvalidTransitionException)
            {
                // A stood-down task has no executable next action at all, which is the end of this loop and the
                // assertion that the next action after Blocked is not another validation.
                actions.Add("None");
                break;
            }

            actions.Add(action.Action);
            if (action.Action == "Validation")
            {
                _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => validator.RunAsync(seed.TaskId, refusedBinding))
                                  .ConfigureAwait(false);
                statusAfterEachValidation.Add((await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Status);
                continue;
            }

            _ = await coder.RunAsync(action.AttemptId!.Value, repositoryBinding).ConfigureAwait(false);
        }

        var trail = string.Join(", ", actions);
        AssertEx.Equal(expected: 2,
            statusAfterEachValidation.Count,
            $"one implementation gets one free re-run of a validation that throws, and then a human: {trail}");
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, statusAfterEachValidation[0], "the FIRST throw is treated as transient and re-run once.");
        AssertEx.Equal(DevelopmentTaskStatus.Blocked, statusAfterEachValidation[1], "the second throw on the same implementation goes to a human.");
        AssertEx.Equal("None", actions[^1], $"and a stood-down task is offered no next action at all, least of all another validation: {trail}");

        var blocked = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.Blocked, blocked.Status);
        var reason = AssertEx.NotNull(blocked.BlockedReason);
        AssertEx.Contains(reason, "failed twice", StringComparison.Ordinal);
        AssertEx.Contains(reason,
            nameof(DevelopmentWorkspaceSecurityException),
            StringComparison.Ordinal,
            "the exception TYPE is named, because its message is the one string on this path nothing has sanitized.");
        AssertEx.False(reason.Contains(repository, StringComparison.OrdinalIgnoreCase), $"and the reason names no host path: {reason}");
    }

    /// <summary>
    ///     The free re-run is per IMPLEMENTATION, not per task: a new coder attempt has not been tried yet, so its
    ///     first throw is transient again. Without that the second implementation of any task that ever hit a
    ///     transient validation fault would be stood down on its first hiccup.
    /// </summary>
    [Test]
    public async Task TheFreeReRunIsCountedPerCoderAttemptAndResetsOnTheNextOne()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        await using var provider = await BuildProviderAsync(new WritingCoderModel("implemented\n"), new ApprovingReviewerModel()).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var coder = scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>();
        var validator = scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>();
        var seed = Seed(repository);
        var repositoryBinding = Binding(seed, repository);
        var refusedBinding = repositoryBinding with
        {
            RepositoryIdentityHash = "not-the-persisted-repository-identity"
        };
        _ = await coordinator.CreateProjectAsync(seed).ConfigureAwait(false);
        var service = CreateManagementService(scope.ServiceProvider);

        var first = await service.StartNextActionAsync(seed.ProjectId, seed.TaskId, Guid.NewGuid()).ConfigureAwait(false);
        _ = await coder.RunAsync(first.AttemptId!.Value, repositoryBinding).ConfigureAwait(false);
        AssertEx.Equal("Validation", (await service.StartNextActionAsync(seed.ProjectId, seed.TaskId, Guid.NewGuid()).ConfigureAwait(false)).Action);
        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => validator.RunAsync(seed.TaskId, refusedBinding)).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Status);

        // A genuinely new implementation, which nothing has tried to validate yet.
        var second = Guid.NewGuid();
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 second,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Coder,
                                 "coder-local",
                                 "local",
                                 (await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false)).Version))
                             .ConfigureAwait(false);
        _ = await coder.RunAsync(second, repositoryBinding).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => validator.RunAsync(seed.TaskId, refusedBinding)).ConfigureAwait(false);

        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress,
            task.Status,
            "the count is keyed on the coder attempt, so a new implementation gets its own free re-run.");
        AssertEx.Null(task.BlockedReason);
    }

    /// <summary>
    ///     The real service over the scope's real store and coordinator. Everything Dev Mode's next-action decision does
    ///     NOT read is a substitute; the supervisor is one deliberately, so the test drives the coder runner and the
    ///     validation runner itself in the order the service asks for them.
    /// </summary>
    private static IDevelopmentManagementService CreateManagementService(IServiceProvider services)
    {
        var supervisor = Substitute.For<IDevelopmentAttemptExecutionSupervisor>();
        _ = supervisor.StartAttempt(Arg.Any<Guid>(), Arg.Any<DevelopmentAttemptRole>()).Returns(true);
        _ = supervisor.StartValidation(Arg.Any<Guid>()).Returns(true);
        return new DevelopmentManagementService(services.GetRequiredService<IDevelopmentStore>(),
            services.GetRequiredService<IDevelopmentCoordinator>(),
            supervisor,
            services.GetRequiredService<IDevelopmentArtifactBlobStore>(),
            services.GetRequiredService<IDevelopmentApplyService>(),
            services.GetRequiredService<IDevelopmentRepositoryBindingService>(),
            Substitute.For<IActiveCloudChatClientFactory>(),
            new FakeModelTrustResolver(),
            Substitute.For<IDevelopmentCommandProfileDetector>(),
            Substitute.For<IDevelopmentProfileBackfillService>(),
            Substitute.For<IDevelopmentTemplateStore>(),
            Substitute.For<IDevWorkflowStore>(),
            Options.Create(new DevWorkflowOptions
            {
                Enabled = true
            }),
            TimeProvider.System,
            NullLogger<DevelopmentManagementService>.Instance);
    }

    private static DevelopmentCommandProfile SlnxProfile() =>
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx,
            DevelopmentSyntheticSolutionRepository.SolutionPath);

    private static DevelopmentCommandProfile CsprojProfile() =>
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetCsproj,
            DevelopmentSyntheticSolutionRepository.ProbeProjectPath);

    /// <summary>
    ///     Drives a coder attempt that rewrites the synthetic solution's library source with
    ///     <paramref name="librarySource" />, then runs deterministic validation under <paramref name="profile" />
    ///     — a real code-owned .NET profile bound to a real build target in the fixture — and returns the outcome
    ///     together with the command evidence the validation report recorded.
    /// </summary>
    private async Task<(DevelopmentValidationResult Validation, DevelopmentTaskSnapshot Task, IReadOnlyList<DevelopmentCommandEvidence> Commands, DevelopmentValidationReport Report)>
        RunDotnetProfileValidationAsync(DevelopmentCommandProfile profile,
            string librarySource,
            bool includeTests = true)
    {
        Directory.CreateDirectory(_root);
        var repository = Path.Combine(_root, "solution-" + Guid.NewGuid().ToString("N"));
        await DevelopmentSyntheticSolutionRepository.CreateAsync(repository, includeTests).ConfigureAwait(false);

        // 10 minutes because MaxAttemptDurationSeconds still bounds the validation run AS A WHOLE (and, with the
        // task's MaxDurationSeconds, is the smaller of the two that wins). Per-command timeouts no longer come from
        // here at all — they are carried by each profile command. The synthetic solution restores, builds and tests
        // in a couple of seconds; the headroom is for a cold NuGet fallback resolve on a loaded machine.
        await using var provider = await BuildProviderAsync(new WritingCoderModel(librarySource, DevelopmentSyntheticSolutionRepository.LibrarySourcePath),
                new ApprovingReviewerModel(),
                maxAttemptDurationSeconds: 600)
            .ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var seed = Seed(repository, profile, maxDurationSeconds: 600);
        var repositoryBinding = Binding(seed, repository);

        _ = await coordinator.CreateProjectAsync(seed).ConfigureAwait(false);
        var ready = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                         Guid.NewGuid(),
                                         DevelopmentTaskStatus.Ready,
                                         ExpectedTaskVersion: 1))
                                     .ConfigureAwait(false);
        var coderAttemptId = Guid.NewGuid();
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 coderAttemptId,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Coder,
                                 "coder-local",
                                 "local",
                                 ready.Version))
                             .ConfigureAwait(false);
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>()
                       .RunAsync(coderAttemptId, repositoryBinding)
                       .ConfigureAwait(false);

        var validation = await scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>()
                                    .RunAsync(seed.TaskId, repositoryBinding)
                                    .ConfigureAwait(false);
        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        var report = await ReadValidationReportAsync(scope.ServiceProvider, validation.ArtifactId, seed.TaskId).ConfigureAwait(false);
        return (validation, task, report.Commands, report);
    }

    /// <summary>Reads back the report the validation runner persisted into its artifact.</summary>
    private static async Task<DevelopmentValidationReport> ReadValidationReportAsync(IServiceProvider services,
        Guid artifactId,
        Guid taskId)
    {
        var artifact = (await services.GetRequiredService<IDevelopmentStore>().ListArtifactsAsync(taskId).ConfigureAwait(false))
            .Single(item => item.Id == artifactId);
        var payload = await services.GetRequiredService<IDevelopmentArtifactBlobStore>()
                                    .ReadAsync(artifact.ProjectId, artifact.Id, artifact.ContentHash, artifact.ByteCount)
                                    .ConfigureAwait(false);
        AssertEx.Equal(DevelopmentArtifactReadStatus.Found, payload.Status);
        var report = JsonSerializer.Deserialize<DevelopmentValidationReport>(payload.Content.Span, ReportJsonOptions);
        return AssertEx.NotNull(report);
    }

    private static int CommandExitCode(IReadOnlyList<DevelopmentCommandEvidence> commands, string commandId) =>
        AssertEx.NotNull(commands.SingleOrDefault(command => string.Equals(command.CommandId, commandId, StringComparison.Ordinal))).ExitCode;

    /// <summary>
    ///     Asserts a profile command succeeded, and on failure reports what the command actually said.
    ///     <para>
    ///         Worth the helper because these commands are real <c>git</c> and <c>dotnet</c> invocations running
    ///         inside the sandbox on whatever host the suite is on, so when one of them fails for an environmental
    ///         reason the bare "Expected: 0, Actual: 1" is close to useless — it names neither the command nor its
    ///         complaint, and the evidence needed to identify it (a NuGet NU-code, a missing executable, a path
    ///         rejection) is sitting right there in the captured streams. Diagnosing that from a CI log on a machine
    ///         you do not have costs a full round-trip per guess.
    ///     </para>
    /// </summary>
    private static void AssertCommandSucceeded(IReadOnlyList<DevelopmentCommandEvidence> commands, string commandId)
    {
        var command = AssertEx.NotNull(commands.SingleOrDefault(candidate => string.Equals(candidate.CommandId, commandId, StringComparison.Ordinal)));
        AssertEx.Equal(expected: 0,
            command.ExitCode,
            $"'{commandId}' was expected to succeed but exited {command.ExitCode} (completed: {command.Completed}).{Environment.NewLine}"
            + $"stderr: {Describe(command.StandardError)}{Environment.NewLine}"
            + $"stdout: {Describe(command.StandardOutput)}");
    }

    private static string Describe(string stream) =>
        string.IsNullOrWhiteSpace(stream) ? "(empty)" : stream.Trim();

    /// <summary>
    ///     Renders the whole validation run — the reported failure plus every command's exit code, and the streams of
    ///     the ones that failed — for the "this was supposed to pass" assertions, where the useful question is not
    ///     which assertion tripped but which command broke and what it said.
    /// </summary>
    private static string DescribeValidation(IReadOnlyList<DevelopmentCommandEvidence> commands, DevelopmentValidationReport report)
    {
        var lines = commands.Select(command => command.ExitCode == 0
            ? $"  {command.CommandId}: exit 0"
            : $"  {command.CommandId}: exit {command.ExitCode} (completed: {command.Completed}){Environment.NewLine}"
              + $"    stderr: {Describe(command.StandardError)}{Environment.NewLine}"
              + $"    stdout: {Describe(command.StandardOutput)}");

        return $"validation did not pass. failureCode: {report.FailureCode ?? "(none)"}, failureDetail: {report.FailureDetail ?? "(none)"}{Environment.NewLine}"
               + string.Join(Environment.NewLine, lines);
    }

    /// <summary>The structured result the code-owned adapter read back from the profile's test command.</summary>
    private static DevelopmentTestOutcome TestOutcome(IReadOnlyList<DevelopmentCommandEvidence> commands) =>
        AssertEx.NotNull(AssertEx.NotNull(commands.SingleOrDefault(static command =>
                                     string.Equals(command.CommandId, DevelopmentCommandIds.DotnetTestRelease, StringComparison.Ordinal)))
                                 .TestOutcome);

    private static async Task<(DevelopmentCreateProjectCommand Seed, Guid ReviewerAttemptId, DevelopmentReviewerAttemptResult Review)> RunThroughReviewAsync(IServiceProvider services,
        string repository)
    {
        var coordinator = services.GetRequiredService<IDevelopmentCoordinator>();
        var store = services.GetRequiredService<IDevelopmentStore>();
        var seed = Seed(repository);
        var repositoryBinding = Binding(seed, repository);
        _ = await coordinator.CreateProjectAsync(seed).ConfigureAwait(false);
        var ready = await coordinator.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                         Guid.NewGuid(),
                                         DevelopmentTaskStatus.Ready,
                                         ExpectedTaskVersion: 1))
                                     .ConfigureAwait(false);
        var coderAttemptId = Guid.NewGuid();
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 coderAttemptId,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Coder,
                                 "coder-local",
                                 "local",
                                 ready.Version))
                             .ConfigureAwait(false);
        _ = await services.GetRequiredService<IDevelopmentCoderAttemptRunner>()
                          .RunAsync(coderAttemptId, repositoryBinding)
                          .ConfigureAwait(false);
        var validation = await services.GetRequiredService<IDevelopmentValidationRunner>()
                                       .RunAsync(seed.TaskId, repositoryBinding)
                                       .ConfigureAwait(false);
        AssertEx.True(validation.Passed);
        var inReview = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        var reviewerAttemptId = Guid.NewGuid();
        _ = await coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                 reviewerAttemptId,
                                 Guid.NewGuid(),
                                 DevelopmentAttemptRole.Reviewer,
                                 "reviewer-local",
                                 "local",
                                 inReview.Version))
                             .ConfigureAwait(false);
        var review = await services.GetRequiredService<IDevelopmentReviewerAttemptRunner>()
                                   .RunAsync(reviewerAttemptId, repositoryBinding)
                                   .ConfigureAwait(false);
        return (seed, reviewerAttemptId, review);
    }

    /// <param name="maxAttemptDurationSeconds">
    ///     The attempt cap, and the ceiling on a whole validation run. It is no longer the per-command timeout: each
    ///     command carries its own budget on the project's command profile. Restore, build and test on the synthetic
    ///     solution need more headroom than a lone `git diff --check` does, so the tests that bind a
    ///     <c>dotnet-slnx</c> profile raise both this and the task's own <c>MaxDurationSeconds</c> — the validation
    ///     runner takes the smaller of the two.
    /// </param>
    private async Task<ServiceProvider> BuildProviderAsync(IDevelopmentCoderModel coderModel,
        IDevelopmentReviewerModel reviewerModel,
        int maxAttemptDurationSeconds = 60)
    {
        Directory.CreateDirectory(_root);
        // Truncated for the same Windows path-budget reason as _root above.
        var dataRoot = Path.Combine(_root, "d-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "node.sqlite");
        var options = Options.Create(new DevelopmentOptions
        {
            Enabled = true,
            MaxArtifactBytes = 2 * 1024 * 1024,
            MaxAttemptDurationSeconds = maxAttemptDurationSeconds,
            MaxToolCalls = 16,
            MaxChangedFiles = 32,
            MaxFileWriteBytes = 1024 * 1024,
            MaxPatchBytes = 1024 * 1024,
            MaxCommandOutputBytes = 256 * 1024,
            MaxOutputTokens = 2048
        });
        var services = new ServiceCollection();

        // Both attempt runners take an ILogger now, to make a swallowed prompt-persistence failure audible. This
        // container is hand-built rather than the host's, so nothing registered the logging services it resolves.
        services.AddLogging();
        services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(dataRoot));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<DevelopmentOptions>>(options);
        // Registered under the Development role, not the bare contract: per-feature selection means nothing
        // resolves ISandboxRuntimeProvider any more, so a bare registration here would build a container in which
        // every Development service failed to resolve its sandbox.
        services.AddSingleton<IDevelopmentSandboxRuntimeProvider>(_ => new RecordingDevelopmentSandbox(new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
                TimeProvider.System),
            _sandboxCreates));
        services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        services.AddDbContext<NodeChatDbContext>((serviceProvider, builder) => builder.UseSqlite($"Data Source={databasePath}")
                                                                                      .EnableServiceProviderCaching(false)
                                                                                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                                                                                      .AddInterceptors(serviceProvider.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                                                                                          serviceProvider.GetRequiredService<NodeEncryptionMaterializationInterceptor>()));
        services.AddScoped<IDevelopmentStore, DevelopmentStore>();
        services.AddSingleton<IDevelopmentArtifactBlobStore, ManagedDevelopmentArtifactBlobStore>();
        services.AddScoped<IDevelopmentWorkspaceSecretsSink, DevelopmentStoreWorkspaceSecretsSink>();
        services.AddScoped<IDevelopmentWorkspaceProvider, DevelopmentWorkspaceProvider>();
        services.AddScoped<IDevelopmentPatchEvidenceService, DevelopmentPatchEvidenceService>();
        services.AddScoped<IDevelopmentEvidenceService, DevelopmentEvidenceService>();
        services.AddScoped(_ => Substitute.For<IDevelopmentRepositoryBindingService>());
        services.AddSingleton(coderModel);
        services.AddSingleton(reviewerModel);
        services.AddSingleton<IDevelopmentCloudAttemptContextService, UnexpectedCloudContextService>();
        services.AddScoped<IDevelopmentCoderAttemptRunner, DevelopmentCoderAttemptRunner>();
        services.AddScoped<IDevelopmentValidationRunner, DevelopmentValidationRunner>();
        services.AddScoped<IDevelopmentReviewerAttemptRunner, DevelopmentReviewerAttemptRunner>();
        services.AddScoped<IDevelopmentHostApplyPort, TrustedDevelopmentHostApplyPort>();
        services.AddScoped<IDevelopmentCoordinator, DevelopmentCoordinator>();
        services.AddScoped<IDevelopmentApplyService, DevelopmentApplyService>();
        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                                                                  INSERT INTO selected_folders (id, alias, host_path, mode, created_at_utc)
                                                                  VALUES ({SelectedFolderId}, {"development-validation-repository"}, {Encoding.UTF8.GetBytes(dataRoot)}, {(int)SelectedFolderMode.Copy}, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()})
                                                                  """).ConfigureAwait(false);
        return provider;
    }

    /// <summary>
    ///     Seeds a project. <paramref name="profile" /> defaults to <c>generic-git</c> — the whitespace check alone —
    ///     so the workflow tests stay fast on a repository that is not a .NET solution at all; the tests that exercise
    ///     the gate itself pass a real <c>dotnet-slnx</c> profile bound to the synthetic buildable solution.
    /// </summary>
    private static DevelopmentCreateProjectCommand Seed(string repository,
        DevelopmentCommandProfile? profile = null,
        int maxDurationSeconds = 60)
    {
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        var resolved = profile
                       ?? DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit,
                           buildTarget: null);
        return new DevelopmentCreateProjectCommand(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Implement a bounded feature.",
            SelectedFolderId,
            DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonical),
            "main",
            "Add feature file",
            "Create feature.txt.",
            "[\"feature.txt exists\"]",
            DevelopmentEgressPolicy.LocalOnly,
            CoderModelId: "coder-local",
            ReviewerModelId: "reviewer-local",
            TrustedRepositoryAcknowledged: true,
            TrustedRepositoryPolicyVersion: DevelopmentTrustPolicy.CurrentVersion,
            TrustedRepositoryAcknowledgedAtUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens: 2048,
            MaxDurationSeconds: maxDurationSeconds,
            CommandProfileJson: Encoding.UTF8.GetString(resolved.ToCanonicalUtf8()));
    }

    private static DevelopmentRepositoryBinding Binding(DevelopmentCreateProjectCommand seed, string repository) =>
        new(seed.ProjectId,
            seed.SelectedFolderId,
            "repository",
            repository,
            seed.RepositoryIdentityHash);

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        EnsureSuccess(await RunProcessAsync(repository, "git", "init", "--initial-branch=main", ".").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "config", "user.email", "development-review@example.invalid").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "config", "user.name", "Development Review Test").ConfigureAwait(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        EnsureSuccess(await RunProcessAsync(repository, "git", "add", "README.md").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "commit", "-m", "base").ConfigureAwait(false));
        return repository;
    }

    private static async Task<CommandResult> RunProcessAsync(string workingDirectory, string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static void EnsureSuccess(CommandResult result) =>
        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);

    private static ILocalModelProviderResolver LocalModelResolver(params LocalModelDescriptor[] models) =>
        LocalModelResolver(servedContextTokens: null, models);

    /// <summary>
    ///     A resolver whose runtime reports <paramref name="servedContextTokens" /> as the window it launched with, or
    ///     nothing — which is the runtime that has no fixed window, and the one that has not started yet.
    ///     <para>
    ///         The window is reported only AFTER the model has been warmed, exactly as the real runtime behaves: it
    ///         knows the launched context of a process it is running and nothing about one it has not started. Delete
    ///         the warm from the budget resolver and every served-window test falls back instead.
    ///     </para>
    /// </summary>
    private static ILocalModelProviderResolver LocalModelResolver(int? servedContextTokens, params LocalModelDescriptor[] models)
    {
        var warmed = false;
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>()).Returns(models);
        provider.When(runtime => runtime.WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(_ => warmed = true);
        provider.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => servedContextTokens is { } served && warmed ? new LocalModelRuntimeInfo(served) : null);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(provider);
        return resolver;
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>
    ///     Passes every call through to the real process backend and records the create requests. A decorator rather
    ///     than a stub on purpose: the point is to observe what a REAL attempt asked for, so replacing the backend
    ///     would replace the thing under test.
    /// </summary>
    private sealed class RecordingDevelopmentSandbox(ProcessSandboxRuntimeProvider inner, List<SandboxCreateRequest> created) : IDevelopmentSandboxRuntimeProvider
    {
        public string ProviderName => inner.ProviderName;

        public SandboxProviderCapabilities Capabilities => inner.Capabilities;

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            created.Add(request);
            return inner.CreateOrAttachAsync(request, cancellationToken);
        }

        public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default) =>
            inner.ConnectAsync(attachKey, cancellationToken);

        public Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default) =>
            inner.ExecuteAsync(handle, request, cancellationToken);

        public Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default) =>
            inner.CopyIntoAsync(handle, request, cancellationToken);

        public Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default) =>
            inner.ReadFileAsync(handle, sandboxPath, cancellationToken);

        public Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default) =>
            inner.CopyOutAsync(handle, request, cancellationToken);

        public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default) =>
            inner.CancelCommandAsync(handle, executionId, cancellationToken);

        public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default) =>
            inner.KillAsync(handle, cancellationToken);
    }

    private sealed class UnexpectedCloudContextService : IDevelopmentCloudAttemptContextService
    {
        public Task<DevelopmentCloudAttemptContext> CreateAsync(DevelopmentExecutionSnapshot snapshot,
            IReadOnlyList<DevelopmentCloudContextExcerpt> excerpts,
            IReadOnlyList<Guid>? inputArtifactIds = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The local-only workflow fixture must not build a cloud context.");
    }

    private sealed class WritingCoderModel(string content, string path = "feature.txt") : IDevelopmentCoderModel
    {
        public async Task<DevelopmentCoderModelResult> RunAsync(string modelId,
            string prompt,
            IDevelopmentWorkspaceTools tools,
            int maxOutputTokens,
            int maxToolCalls,
            DevelopmentAttemptLiveProgress? liveProgress = null,
            DevelopmentCloudRoleRoute? cloudRoute = null,
            CancellationToken cancellationToken = default)
        {
            _ = await tools.WriteFileAsync(path, content, cancellationToken).ConfigureAwait(false);
            return new DevelopmentCoderModelResult(new DevelopmentCoderSubmission("Implemented feature file.",
                    [path],
                    [],
                    Notes: null),
                InputTokens: 10,
                OutputTokens: 10);
        }
    }

    private sealed class ApprovingReviewerModel : IDevelopmentReviewerModel
    {
        public Task<DevelopmentReviewerModelResult> RunAsync(string modelId,
            string prompt,
            IDevelopmentWorkspaceTools tools,
            int maxOutputTokens,
            int maxToolCalls,
            DevelopmentAttemptLiveProgress? liveProgress = null,
            DevelopmentCloudRoleRoute? cloudRoute = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevelopmentReviewerModelResult(new DevelopmentReviewerSubmission(DevelopmentReviewDisposition.Approved,
                    "The exact validated subject satisfies the acceptance criterion.",
                    []),
                InputTokens: 10,
                OutputTokens: 10));
    }

    private sealed class ChangesRequestingReviewerModel : IDevelopmentReviewerModel
    {
        public Task<DevelopmentReviewerModelResult> RunAsync(string modelId,
            string prompt,
            IDevelopmentWorkspaceTools tools,
            int maxOutputTokens,
            int maxToolCalls,
            DevelopmentAttemptLiveProgress? liveProgress = null,
            DevelopmentCloudRoleRoute? cloudRoute = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevelopmentReviewerModelResult(new DevelopmentReviewerSubmission(DevelopmentReviewDisposition.ChangesRequested,
                    "The implementation needs a correction.",
                    [new DevelopmentReviewFinding("correctness", "The fixture reviewer requested a deterministic change.")]),
                InputTokens: 10,
                OutputTokens: 10));
    }

    private sealed class CredentialLeakingReviewerModel : IDevelopmentReviewerModel
    {
        public Task<DevelopmentReviewerModelResult> RunAsync(string modelId,
            string prompt,
            IDevelopmentWorkspaceTools tools,
            int maxOutputTokens,
            int maxToolCalls,
            DevelopmentAttemptLiveProgress? liveProgress = null,
            DevelopmentCloudRoleRoute? cloudRoute = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevelopmentReviewerModelResult(new DevelopmentReviewerSubmission(DevelopmentReviewDisposition.Approved,
                    "password=!Sensitive12345678",
                    []),
                InputTokens: 10,
                OutputTokens: 10));
    }

    /// <summary>A reviewer that mis-spells the disposition once, reads what came back, and calls again correctly.</summary>
    private sealed class CorrectingReviewerChatClient : IChatClient
    {
        public string? FirstAnswer { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var submit = AssertEx.NotNull(options?.Tools?.OfType<AIFunction>().SingleOrDefault(static tool => tool.Name == "submit_review"));
            FirstAnswer = (await submit.InvokeAsync(new AIFunctionArguments
            {
                ["disposition"] = "approve",
                ["summary"] = "looks fine",
                ["findings"] = Array.Empty<DevelopmentReviewFinding>()
            }, cancellationToken).ConfigureAwait(false))?.ToString();
            _ = await submit.InvokeAsync(new AIFunctionArguments
            {
                ["disposition"] = "Approved",
                ["summary"] = "looks fine",
                ["findings"] = Array.Empty<DevelopmentReviewFinding>()
            }, cancellationToken).ConfigureAwait(false);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "approved"))
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

    private sealed class CapturingReviewerChatClient(long inputTokens = 10, long outputTokens = 10) : IChatClient
    {
        public HashSet<string> ToolNames { get; } = new(StringComparer.Ordinal);

        /// <summary>The round's own options, and the provider-call budget the attempt opened around it.</summary>
        public ChatOptions? Options { get; private set; }

        public ProviderCallBudgetOptions? Budget { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            Budget = ProviderCallBudget.Current?.Options;
            foreach (var tool in options?.Tools?.OfType<AIFunction>() ?? [])
            {
                ToolNames.Add(tool.Name);
            }

            var submit = AssertEx.NotNull(options?.Tools?.OfType<AIFunction>().SingleOrDefault(tool => tool.Name == "submit_review"));
            _ = await submit.InvokeAsync(new AIFunctionArguments
            {
                ["disposition"] = "Approved",
                ["summary"] = "approved",
                ["findings"] = Array.Empty<DevelopmentReviewFinding>()
            }, cancellationToken).ConfigureAwait(false);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "approved"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens,
                    TotalTokenCount = inputTokens + outputTokens
                }
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            _ = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "approved");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens,
                    TotalTokenCount = inputTokens + outputTokens
                })
            ]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            null;

        public void Dispose() { }
    }

    private sealed class NullWorkspaceTools : IDevelopmentWorkspaceTools
    {
        public IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence => [];

        /// <summary>
        ///     The reviewer-model tests never run a command, so the cheapest real profile is the right stub — the
        ///     generic one, which needs no build target.
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

        public Task<string> ApplyPatchAsync(string patch, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> GetDiffAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> RunCommandAsync(string commandId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }
}
