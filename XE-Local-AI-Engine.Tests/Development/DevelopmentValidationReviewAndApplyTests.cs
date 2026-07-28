namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevelopmentValidationReviewAndApplyTests : IDisposable
{
    private static readonly Guid SelectedFolderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    /// <summary>The shipped production validation profile, joined so order-sensitive comparison reads clearly.</summary>
    private static readonly string ExpectedDefaultProfile = string.Join(',',
        DevelopmentCommandIds.GitDiffCheck,
        DevelopmentCommandIds.DotnetRestore,
        DevelopmentCommandIds.DotnetBuildRelease,
        DevelopmentCommandIds.DotnetTestRelease);

    /// <summary>Reused because CA1869 forbids minting serializer options per operation.</summary>
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-validation-review-" + Guid.NewGuid().ToString("N"));

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
    public async Task ValidationFailure_ReturnsTaskToInProgressAndCannotStartReviewer()
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
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, task.Status);
        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => coordinator.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                          Guid.NewGuid(),
                          Guid.NewGuid(),
                          DevelopmentAttemptRole.Reviewer,
                          "reviewer-local",
                          "local",
                          task.Version)))
                      .ConfigureAwait(false);
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
            }));

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

        var (seed, _, review) = await RunThroughReviewAsync(scope.ServiceProvider, repository).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentReviewDisposition.ChangesRequested, review.Disposition);
        AssertEx.Equal(DevelopmentTaskStatus.ChangesRequested, review.TaskStatus);
        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => scope.ServiceProvider.GetRequiredService<IDevelopmentApplyService>()
                                                                                     .PreviewAsync(seed.TaskId, Binding(seed, repository)))
                      .ConfigureAwait(false);
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
        // better than the whole report being rejected. Narrowing the fallback so ordinary identifiers survive is
        // follow-up work on the shared scanner.
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

    [Test]
    public async Task ReviewerModel_AllowsLargeInputWithinOutputCapAndRejectsOutputCapPlusOne()
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
            resolver);

        var result = await exact.RunAsync("reviewer-local",
            "review exact subject",
            new NullWorkspaceTools(),
            maxOutputTokens: 64,
            maxToolCalls: 8).ConfigureAwait(false);

        AssertEx.Equal<long?>(40_000, result.InputTokens);
        AssertEx.Equal<long?>(64, result.OutputTokens);

        using var overChat = new CapturingReviewerChatClient(inputTokens: 40_000, outputTokens: 65);
        var over = new DevelopmentReviewerModel(overChat, cloud, resolver);
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => over.RunAsync("reviewer-local",
                          "review exact subject",
                          new NullWorkspaceTools(),
                          maxOutputTokens: 64,
                          maxToolCalls: 8))
                      .ConfigureAwait(false);
    }

    /// <summary>
    ///     The shipped default is what the gate actually runs — <c>appsettings.json</c> sets only
    ///     <c>Development:Enabled</c>, and <c>ValidationCommandIds</c> cannot be overridden from configuration at all
    ///     (an init-only <c>IReadOnlyList&lt;string&gt;</c> with a non-empty default is silently ignored by
    ///     <c>ConfigurationBinder</c>). So this default IS the production profile on every install, and it regressing
    ///     to a whitespace check is exactly the defect the tests below exist to keep fixed.
    /// </summary>
    [Test]
    public async Task DefaultValidationProfile_RunsWhitespaceRestoreBuildAndTestInDependencyOrder()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        AssertEx.Equal(ExpectedDefaultProfile, string.Join(',', new DevelopmentOptions().ValidationCommandIds));
    }

    [Test]
    public async Task Validation_UnderTheDefaultProfile_WhenPatchCompilesAndPasses_ReachesInReviewWithEveryCommandRecorded()
    {
        var (validation, task, commands) = await RunDefaultProfileValidationAsync(DevelopmentSyntheticSolutionRepository.PassingLibrarySource)
            .ConfigureAwait(false);

        AssertEx.True(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, task.Status);
        AssertEx.Equal(ExpectedDefaultProfile, string.Join(',', commands.Select(static command => command.CommandId)));
        AssertEx.True(commands.All(static command => command.Completed && command.ExitCode == 0));
    }

    /// <summary>
    ///     The first of the two outcomes the shipped one-command profile silently accepted: a patch that does not
    ///     compile. Under the old default this reached <c>InReview</c> and could be applied to the trusted repository.
    /// </summary>
    [Test]
    public async Task Validation_UnderTheDefaultProfile_WhenPatchBreaksTheBuild_DoesNotReachInReview()
    {
        var (validation, task, commands) = await RunDefaultProfileValidationAsync(DevelopmentSyntheticSolutionRepository.BuildBreakingLibrarySource)
            .ConfigureAwait(false);

        AssertEx.False(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, task.Status);

        // The build is what rejected it: the whitespace check and the restore ahead of it both succeeded, and the
        // test command never ran because the runner stops at the first failure.
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.GitDiffCheck));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetRestore));
        AssertEx.NotEqual(notExpected: 0, CommandExitCode(commands,DevelopmentCommandIds.DotnetBuildRelease));
    }

    /// <summary>
    ///     The second outcome the shipped one-command profile silently accepted: a patch that compiles cleanly and
    ///     fails its test. This one is invisible to every check the old default performed.
    /// </summary>
    [Test]
    public async Task Validation_UnderTheDefaultProfile_WhenPatchFailsATest_DoesNotReachInReview()
    {
        var (validation, task, commands) = await RunDefaultProfileValidationAsync(DevelopmentSyntheticSolutionRepository.TestFailingLibrarySource)
            .ConfigureAwait(false);

        AssertEx.False(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, task.Status);

        // The change compiles — only the test rejected it, which is the signal the old default could not see at all.
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.GitDiffCheck));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetRestore));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetBuildRelease));
        AssertEx.NotEqual(notExpected: 0, CommandExitCode(commands,DevelopmentCommandIds.DotnetTestRelease));
    }

    /// <summary>
    ///     Drives a coder attempt that rewrites the synthetic solution's library source with
    ///     <paramref name="librarySource" />, then runs deterministic validation under the shipped production command
    ///     profile and returns the outcome together with the command evidence the validation report recorded.
    /// </summary>
    private async Task<(DevelopmentValidationResult Validation, DevelopmentTaskSnapshot Task, IReadOnlyList<DevelopmentCommandEvidence> Commands)> RunDefaultProfileValidationAsync(string librarySource)
    {
        Directory.CreateDirectory(_root);
        var repository = Path.Combine(_root, "solution-" + Guid.NewGuid().ToString("N"));
        await DevelopmentSyntheticSolutionRepository.CreateAsync(repository).ConfigureAwait(false);

        // 10 minutes because MaxAttemptDurationSeconds is the PER-COMMAND timeout as well as the attempt cap, and
        // this profile now spends four commands against it. The synthetic solution restores, builds and tests in a
        // couple of seconds; the headroom is for a cold NuGet fallback resolve on a loaded machine.
        await using var provider = await BuildProviderAsync(new WritingCoderModel(librarySource, DevelopmentSyntheticSolutionRepository.LibrarySourcePath),
                                       new ApprovingReviewerModel(),
                                       new DevelopmentOptions().ValidationCommandIds,
                                       maxAttemptDurationSeconds: 600)
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
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>()
                       .RunAsync(coderAttemptId, repositoryBinding)
                       .ConfigureAwait(false);

        var validation = await scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>()
                                    .RunAsync(seed.TaskId, repositoryBinding)
                                    .ConfigureAwait(false);
        var task = await store.GetTaskAsync(seed.TaskId).ConfigureAwait(false);
        var commands = await ReadValidationCommandsAsync(scope.ServiceProvider, validation.ArtifactId, seed.TaskId).ConfigureAwait(false);
        return (validation, task, commands);
    }

    /// <summary>Reads back the command evidence the validation runner persisted into its report artifact.</summary>
    private static async Task<IReadOnlyList<DevelopmentCommandEvidence>> ReadValidationCommandsAsync(IServiceProvider services,
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
        return AssertEx.NotNull(report).Commands;
    }

    private static int CommandExitCode(IReadOnlyList<DevelopmentCommandEvidence> commands, string commandId) =>
        AssertEx.NotNull(commands.SingleOrDefault(command => string.Equals(command.CommandId, commandId, StringComparison.Ordinal))).ExitCode;

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

    /// <param name="validationCommandIds">
    ///     The deterministic validation profile for this fixture. It defaults to the whitespace check alone so the
    ///     workflow tests stay fast on a repository that is not a .NET solution; the tests that exercise the gate
    ///     itself pass the shipped production default and bind to a synthetic buildable solution instead.
    /// </param>
    /// <param name="maxAttemptDurationSeconds">
    ///     The per-command timeout, which is also the attempt cap — they are the same knob today. Restore, build and
    ///     test on the synthetic solution need more headroom than a lone `git diff --check` does.
    /// </param>
    private async Task<ServiceProvider> BuildProviderAsync(IDevelopmentCoderModel coderModel,
        IDevelopmentReviewerModel reviewerModel,
        IReadOnlyList<string>? validationCommandIds = null,
        int maxAttemptDurationSeconds = 60)
    {
        Directory.CreateDirectory(_root);
        var dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
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
            MaxOutputTokens = 2048,
            ValidationCommandIds = validationCommandIds ?? [DevelopmentCommandIds.GitDiffCheck]
        });
        var services = new ServiceCollection();
        services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(dataRoot));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<DevelopmentOptions>>(options);
        services.AddSingleton<ISandboxRuntimeProvider>(_ => new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
            TimeProvider.System));
        services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        services.AddDbContext<NodeChatDbContext>((serviceProvider, builder) => builder.UseSqlite($"Data Source={databasePath}")
                                                                                      .EnableServiceProviderCaching(false)
                                                                                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                                                                                      .AddInterceptors(serviceProvider.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                                                                                          serviceProvider.GetRequiredService<NodeEncryptionMaterializationInterceptor>()));
        services.AddScoped<IDevelopmentStore, DevelopmentStore>();
        services.AddSingleton<IDevelopmentArtifactBlobStore, ManagedDevelopmentArtifactBlobStore>();
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

    private static DevelopmentCreateProjectCommand Seed(string repository)
    {
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
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
            MaxDurationSeconds: 60);
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

    private static ILocalModelProviderResolver LocalModelResolver(params LocalModelDescriptor[] models)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>()).Returns(models);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(provider);
        return resolver;
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

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

    private sealed class CapturingReviewerChatClient(long inputTokens = 10, long outputTokens = 10) : IChatClient
    {
        public HashSet<string> ToolNames { get; } = new(StringComparer.Ordinal);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
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
