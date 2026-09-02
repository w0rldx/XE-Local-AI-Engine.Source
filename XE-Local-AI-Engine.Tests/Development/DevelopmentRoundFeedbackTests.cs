namespace XE_Local_AI_Engine.Tests.Development;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     What a REWORK round is told.
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
