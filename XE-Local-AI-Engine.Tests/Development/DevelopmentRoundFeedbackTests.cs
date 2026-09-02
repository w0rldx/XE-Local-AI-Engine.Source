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
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null));

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
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null));

        AssertEx.False(prompt.Contains("Feedback from the previous round", StringComparison.Ordinal),
            "a first round has nothing to be told, and an empty heading would read as one.");
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

    private static DevelopmentExecutionSnapshot Snapshot(string? previousRoundFeedback) =>
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
            previousRoundFeedback);
}
