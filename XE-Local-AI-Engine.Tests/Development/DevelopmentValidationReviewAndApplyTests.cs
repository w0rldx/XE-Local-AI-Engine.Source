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

        AssertEx.True(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, task.Status);
        AssertEx.Equal(ExpectedDotnetValidationProfile, string.Join(',', commands.Select(static command => command.CommandId)));
        AssertEx.True(commands.All(static command => command.Completed && command.ExitCode == 0));

        // Slice 4: the gate no longer just knows the test command exited zero, it knows what the suite did. The
        // fixture has exactly one test, and it ran.
        var outcome = TestOutcome(commands);
        AssertEx.True(outcome.Parsed);
        AssertEx.Equal(expected: 1, outcome.Discovered);
        AssertEx.Equal(expected: 1, outcome.Executed);
        AssertEx.Equal(expected: 1, outcome.Passed);
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
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, task.Status);

        // The build is what rejected it: the whitespace check and the restore ahead of it both succeeded.
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.GitDiffCheck));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetRestore));
        AssertEx.NotEqual(notExpected: 0, CommandExitCode(commands,DevelopmentCommandIds.DotnetBuildRelease));

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
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, task.Status);

        // The change compiles — only the test rejected it, which is the signal the old default could not see at all.
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.GitDiffCheck));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetRestore));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetBuildRelease));
        AssertEx.NotEqual(notExpected: 0, CommandExitCode(commands,DevelopmentCommandIds.DotnetTestRelease));

        // Slice 4: the report now says WHAT failed, not merely that something did. One test ran and it failed.
        var outcome = TestOutcome(commands);
        AssertEx.True(outcome.Parsed);
        AssertEx.Equal(expected: 1, outcome.Executed);
        AssertEx.Equal(expected: 0, outcome.Passed);
        AssertEx.Equal(expected: 1, outcome.Failed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.TestsFailed, report.FailureCode);
    }

    /// <summary>
    ///     S4.4 — the policy for a registered repository that has no tests at all.
    ///     <para>
    ///         Its build target compiles perfectly well; there is simply nothing to run. Before Slice 4 the gate had
    ///         no opinion about that beyond the test command's exit code, and a runner that answered zero would have
    ///         been accepted as a pass. The rule now is that a change cannot be validated by a suite that ran nothing,
    ///         and — because this is a state the operator can actually fix — it is reported with its own code rather
    ///         than collapsed into "validation failed".
    ///     </para>
    ///     <para>
    ///         D3 is what makes the failure recoverable rather than a dead end: the agent may ADD tests, it just may
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
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, task.Status);

        // Everything up to the test command is genuinely green — this is not a build failure wearing a different hat.
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.GitDiffCheck));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetRestore));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetBuildRelease));

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

        AssertEx.True(validation.Passed);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InReview, task.Status);
        AssertEx.Equal(ExpectedDotnetValidationProfile, string.Join(',', commands.Select(static command => command.CommandId)));
        AssertEx.True(commands.All(static command => command.Completed && command.ExitCode == 0));

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
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, validation.TaskStatus);
        AssertEx.Equal(DevelopmentTaskStatus.InProgress, task.Status);

        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.GitDiffCheck));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetRestore));
        AssertEx.Equal(expected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetBuildRelease));
        AssertEx.NotEqual(notExpected: 0, CommandExitCode(commands, DevelopmentCommandIds.DotnetTestRelease));

        AssertEx.Equal(expected: 1, TestOutcome(commands).Failed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.TestsFailed, report.FailureCode);
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
    private async Task<(DevelopmentValidationResult Validation, DevelopmentTaskSnapshot Task, IReadOnlyList<DevelopmentCommandEvidence> Commands, DevelopmentValidationReport Report)> RunDotnetProfileValidationAsync(DevelopmentCommandProfile profile,
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
            MaxOutputTokens = 2048
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
