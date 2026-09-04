namespace XE_Local_AI_Engine.Tests.Development;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     What a REWORK round is told, and what a refused reviewer attempt says.
///     <para>
///         A coder attempt was composed from the task's title, requirements and acceptance criteria and nothing else —
///         so a round asked for BECAUSE the previous one was wrong was handed the identical brief and re-implemented
///         blind. That is true of an ordinary Dev Mode rework round as much as of a workflow's routed one, and both now
///         travel down the same channel: whatever asked for the rework, its reason reaches the round that must act.
///     </para>
/// </summary>
public sealed class DevelopmentRoundFeedbackTests
{
    [Test]
    public void TheCoderPromptCarriesTheReasonTheLastRoundWasRejectedFor()
    {
        var prompt = DevelopmentCoderAttemptRunner.BuildPrompt(Snapshot("Node 'validate' rejected this implementation: 3 of 15 tests failed."),
            Session(),
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null),
            Carried());

        AssertEx.Contains(prompt, "Feedback from the previous round:");
        AssertEx.Contains(prompt, "3 of 15 tests failed");

        // Still the whole brief: the feedback is added to what the round is told, never in place of it.
        AssertEx.Contains(prompt, "It has to do the thing.");
    }

    [Test]
    public void WithNoPreviousRound_TheCoderPromptSaysNothingAboutOne()
    {
        var prompt = DevelopmentCoderAttemptRunner.BuildPrompt(Snapshot(previousRoundFeedback: null),
            Session(),
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null),
            Carried());

        AssertEx.False(prompt.Contains("Feedback from the previous round", StringComparison.Ordinal),
            "a first round has nothing to be told, and an empty heading would read as one.");
    }

    /// <summary>
    ///     An ordinary Dev Mode rework round is told what the reviewer actually found, not that it found something.
    ///     The fixed sentence alone left the same hole the workflow's routed change requests had.
    /// </summary>
    [Test]
    public void AReviewersChangeRequestCarriesItsFindingsIntoTheNextRoundsPrompt()
    {
        var reason = DevelopmentReviewerAttemptRunner.ChangeRequestReason(new DevelopmentReviewerSubmission(DevelopmentReviewDisposition.ChangesRequested,
            "The patch does not cover the failing case.",
            [
                new DevelopmentReviewFinding("correctness", "ParseBound returns the low bound when the range is inverted."),
                new DevelopmentReviewFinding("tests", "No test covers an inverted range.")
            ]));

        var prompt = DevelopmentCoderAttemptRunner.BuildPrompt(Snapshot(reason),
            Session(),
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null),
            Carried());

        AssertEx.Contains(prompt, "Feedback from the previous round:");
        AssertEx.Contains(prompt, "ParseBound returns the low bound when the range is inverted.");
        AssertEx.Contains(prompt, "No test covers an inverted range.");
    }

    /// <summary>A reviewer that found nothing to name still has the fixed sentence, which is all it can honestly say.</summary>
    [Test]
    public void WithNoFindings_AReviewersChangeRequestFallsBackToTheFixedSentence()
    {
        AssertEx.Equal("The independent reviewer requested changes.",
            DevelopmentReviewerAttemptRunner.ChangeRequestReason(new DevelopmentReviewerSubmission(DevelopmentReviewDisposition.ChangesRequested, "Summary", [])));
    }

    /// <summary>
    ///     The mirror of the coder path's fix: a reviewer attempt refused by a workspace policy carries the POLICY's own
    ///     sentence behind the shared failure code, instead of the generic "violated a workspace security policy" line
    ///     that told an operator nothing to change and that a workflow node could not read as a Policy stand-down.
    /// </summary>
    [Test]
    public void AReviewerRefusedByAWorkspacePolicySaysWhichPolicyRefusedIt()
    {
        var reason = DevelopmentReviewerAttemptRunner.SanitizedReason(new DevelopmentWorkspaceSecurityException("The repository trust acknowledgement has expired."));

        AssertEx.Contains(reason, "trust acknowledgement has expired");
        AssertEx.True(DevelopmentAttemptEvidenceException.Names(reason, DevelopmentAttemptFailureCodes.WorkspacePolicyRefused),
            "a workflow node reads the code, not the sentence, to know a retry cannot change the answer.");
    }

    /// <summary>A policy message the sanitizer refuses is not surfaced — the generic reviewer line is what is left.</summary>
    [Test]
    public void AReviewerPolicyMessageTheSanitizerRefusesFallsBackToTheGenericLine()
    {
        var reason = DevelopmentReviewerAttemptRunner.SanitizedReason(new DevelopmentWorkspaceSecurityException(
            "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEAvV5s\n-----END RSA PRIVATE KEY-----"));

        AssertEx.Contains(reason, "The Development reviewer attempt violated a workspace security policy.");
        AssertEx.False(reason.Contains("PRIVATE KEY", StringComparison.Ordinal), "the refused material must not travel with the refusal.");
    }

    /// <summary>
    ///     A DevTask node run's rule sets reach the coder the workflow routed the round to. The workflow already
    ///     bounded and rendered the sections; what is asserted here is that the prompt carries them at all, which it
    ///     did not while policy injection named the agent lane alone.
    /// </summary>
    [Test]
    public void TheCoderPromptCarriesThePolicyTheWorkflowResolvedForTheTask()
    {
        var prompt = DevelopmentCoderAttemptRunner.BuildPrompt(Snapshot(previousRoundFeedback: null, WorkflowPolicy),
            Session(),
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null),
            Carried());

        AssertEx.Contains(prompt, "Policy (rule sets applied by the workflow):");
        AssertEx.Contains(prompt, "## Policy: House rules");
        AssertEx.Contains(prompt, "Never touch production without an approved plan.");

        // Still the whole brief: the policy is added to what the round is told, never in place of it.
        AssertEx.Contains(prompt, "It has to do the thing.");
    }

    [Test]
    public void WithNoWorkflowPolicy_TheCoderPromptSaysNothingAboutOne()
    {
        var prompt = DevelopmentCoderAttemptRunner.BuildPrompt(Snapshot(previousRoundFeedback: null),
            Session(),
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null),
            Carried());

        AssertEx.False(prompt.Contains("Policy (rule sets applied by the workflow)", StringComparison.Ordinal),
            "an ordinary Dev Mode task has no workflow governing it, and an empty heading would read as one that said nothing.");
    }

    /// <summary>
    ///     The reviewer is governed by the same rule sets as the coder it judges. A reviewer held to a different
    ///     standard than the round it reviews would reject correct work for a rule nobody was given, or pass work that
    ///     broke one.
    /// </summary>
    [Test]
    public void TheReviewerPromptCarriesTheSamePolicyTheCoderWasGiven()
    {
        var prompt = DevelopmentReviewerAttemptRunner.BuildPrompt(Snapshot(previousRoundFeedback: null, WorkflowPolicy), Task(), Validation());

        AssertEx.Contains(prompt, "Policy (rule sets applied by the workflow):");
        AssertEx.Contains(prompt, "Never touch production without an approved plan.");
        AssertEx.Contains(prompt, "It has to do the thing.", message: "still the whole brief the coder was judged against.");
    }

    [Test]
    public void WithNoWorkflowPolicy_TheReviewerPromptSaysNothingAboutOne()
    {
        var prompt = DevelopmentReviewerAttemptRunner.BuildPrompt(Snapshot(previousRoundFeedback: null), Task(), Validation());

        AssertEx.False(prompt.Contains("Policy (rule sets applied by the workflow)", StringComparison.Ordinal),
            "a review of an ordinary Dev Mode task has no workflow policy to hold it to.");
    }

    /// <summary>
    ///     A rework round is told WHICH files the shared workspace already carries, not only that such files exist.
    ///     Live on 2026-09-04 a coder reverted a file an earlier attempt had created, reported it, and lost the whole
    ///     attempt to changed_file_manifest_mismatch: it had the rule and not the data, and each attempt is a fresh
    ///     conversation that cannot remember the round before it.
    /// </summary>
    [Test]
    public void TheCoderPromptNamesTheFilesTheSharedWorkspaceAlreadyCarries()
    {
        var prompt = DevelopmentCoderAttemptRunner.BuildPrompt(Snapshot(previousRoundFeedback: null),
            Session(),
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null),
            Carried("src/Calculator.cs", "tests/CalculatorTests.cs"));

        AssertEx.Contains(prompt, "Files in this shared workspace that already differ from the base commit");
        AssertEx.Contains(prompt, "src/Calculator.cs, tests/CalculatorTests.cs");
        AssertEx.Contains(prompt, "a file you revert or delete back to the base commit is NOT a changed file");
    }

    /// <summary>A task's first attempt carries nothing, and a heading over an empty list would read as one that did.</summary>
    [Test]
    public void WithAnEmptyWorkspace_TheCoderPromptSaysNothingAboutCarriedFiles()
    {
        var prompt = DevelopmentCoderAttemptRunner.BuildPrompt(Snapshot(previousRoundFeedback: null),
            Session(),
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null),
            Carried());

        AssertEx.False(prompt.Contains("Files in this shared workspace that already differ", StringComparison.Ordinal),
            "a first attempt has no earlier attempt to have left anything behind.");
    }

    /// <summary>
    ///     The list is bounded and ordered. MaxChangedFiles allows 256 paths, and a prompt is a context window: the
    ///     coder is given the first twenty in a stable order and told how many it is not being shown.
    /// </summary>
    [Test]
    public void TheCarriedFileListIsOrderedAndBounded()
    {
        var carried = Carried([.. Enumerable.Range(0, 23).Select(index => $"src/File{index:D2}.cs")]);

        var prompt = DevelopmentCoderAttemptRunner.BuildPrompt(Snapshot(previousRoundFeedback: null),
            Session(),
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null),
            carried);

        AssertEx.Contains(prompt, "src/File00.cs, src/File01.cs");
        AssertEx.Contains(prompt, "src/File19.cs (+3 more)");
        AssertEx.False(prompt.Contains("src/File20.cs", StringComparison.Ordinal), "the twenty-first path is past the bound.");
    }

    /// <summary>What an earlier attempt on the same task left in the shared workspace.</summary>
    private static IReadOnlySet<string> Carried(params string[] paths) =>
        paths.ToHashSet(StringComparer.Ordinal);

    /// <summary>What a workflow renders onto the task: a heading the audit names and the body it snapshotted.</summary>
    private const string WorkflowPolicy = "## Policy: House rules\nNever touch production without an approved plan.";

    private static DevelopmentTaskSnapshot Task() =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            "Add the feature",
            "It has to do the thing.",
            "[\"it does the thing\"]",
            DevelopmentTaskStatus.InReview,
            CurrentReviewRound: 1,
            MaxReviewRounds: 3,
            BlockedReason: null,
            BlockedAtUtc: null,
            ApprovedSubjectHash: null,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            Version: 1);

    private static DevelopmentValidationReport Validation() =>
        new(Passed: true,
            "0123456789abcdef0123456789abcdef01234567",
            "subject-hash",
            "manifest-hash",
            "result-hash",
            "1",
            DevelopmentCommandProfileCatalog.GenericGit,
            "digest",
            FailureCode: null,
            FailureDetail: null,
            Commands: [],
            CompletedAtUtc: 0);

    private static DevelopmentWorkspaceSession Session() =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "0123456789abcdef0123456789abcdef01234567",
            "identity",
            "/worktree",
            "/runtime",
            new SandboxHandle
            {
                ProviderName = "test",
                SandboxId = "sandbox-1",
                AttachKey = new SandboxAttachKey
                {
                    OwnerUserId = "owner",
                    NodeId = "node",
                    ProviderName = "test",
                    RuntimeProfile = "development-local",
                    ManifestVersion = 1
                },
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = 1
            });

    private static DevelopmentExecutionSnapshot Snapshot(string? previousRoundFeedback, string? workflowPolicyText = null) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "identity",
            "main",
            DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion: 1,
            TrustedRepositoryAcknowledged: true,
            DevelopmentTrustPolicy.CurrentVersion,
            DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds(),
            MaxTokens: 2048,
            MaxDurationSeconds: 60,
            "Add the feature",
            "It has to do the thing.",
            "[\"it does the thing\"]",
            DevelopmentTaskStatus.ChangesRequested,
            TaskVersion: 1,
            DevelopmentAttemptRole.Coder,
            PersistenceDevelopmentAttemptStatus.Running,
            "local-model",
            "local",
            AttemptVersion: 1,
            CommandProfileJson: null,
            previousRoundFeedback,
            workflowPolicyText);
}
