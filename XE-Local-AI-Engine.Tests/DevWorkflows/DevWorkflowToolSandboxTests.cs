namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Slice B1 gate, against a REAL sandbox: a tool node prepares a workspace for a node run that has no Dev Mode
///     task behind it, runs a real <c>dotnet restore</c> and <c>dotnet build</c> in it, and produces a sanitized report.
///     <para>
///         Slow and environment-bound by construction — it clones a repository and builds a solution, so it needs Git
///         and the .NET SDK, and it restores offline from this host's own package cache the way
///         <see cref="DevelopmentSyntheticSolutionRepository" /> arranges for every other gate test in this repository.
///         It is deliberately the only test in Slice B that does this; everything else runs against the scripted lane.
///     </para>
/// </summary>
public sealed class DevWorkflowToolSandboxTests : IDisposable
{
    /// <summary>
    ///     Three of the four .NET profile commands: the whitespace check, the restore and the build. The test command is
    ///     left out through the NODE's own list, which is the definition-authored override — so this exercises that
    ///     override and the verdict's "every declared command produced evidence" rule agreeing about which list applies.
    ///     <para>
    ///         <c>maxAttempts</c> is 1 deliberately: a build that does not compile is retryable, and at the default of
    ///         three this test would clone and build the same broken tree three times to learn what it learns on the
    ///         first. One attempt is also what makes it the exhaustion case against a REAL build — the node has nothing
    ///         left to try, so it stands down for a human.
    ///     </para>
    /// </summary>
    private const string BuildOnlyToolGraph = """
                                              {
                                                "schemaVersion": 1,
                                                "nodes": [
                                                  { "nodeKey": "build", "nodeType": "Tool", "label": "Build the solution", "maxAttempts": 1,
                                                    "validationCommandIds": ["git_diff_check", "dotnet_restore", "dotnet_build_release_no_restore"] }
                                                ],
                                                "edges": []
                                              }
                                              """;

    /// <summary>
    ///     The whole four-command profile under a budget nothing that really compiles and tests a solution can finish
    ///     inside.
    ///     <para>
    ///         One second, and the four commands rather than three, so the assertion holds in the direction machines
    ///         vary in: a slower host runs out of time sooner, and the only way this could stop timing out is a host
    ///         that restores, builds AND tests a real solution inside a second. <c>maxAttempts</c> is 1, so the node
    ///         stands down instead of spending a second workspace on the same clock.
    ///     </para>
    /// </summary>
    private const string ImpatientBuildGraph = """
                                               {
                                                 "schemaVersion": 1,
                                                 "nodes": [
                                                   { "nodeKey": "build", "nodeType": "Tool", "label": "Build and test", "maxAttempts": 1,
                                                     "nodeTimeoutSeconds": 1 }
                                                 ],
                                                 "edges": []
                                               }
                                               """;

    // Short prefix on purpose: the workspace provider appends development/workspaces/<projectId>/<attempt id> as two
    // more full GUIDs under this root, and a long one pushes `git clone` past the path limit on Windows.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-dw-tool-" + Guid.NewGuid().ToString("N")[..12]);

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

    /// <summary>
    ///     A real build that succeeds, and one that does not, over the same repository — the two answers the gate has to
    ///     be able to tell apart. Both runs go through the whole lane: a prepared workspace keyed on the node run, the
    ///     profile's own commands, the deterministic verdict, and a report kept as a run artifact.
    /// </summary>
    [Test]
    public async Task ARealBuildToolNodePassesOrFailsAndKeepsASanitizedReport()
    {
        var repository = Path.Combine(_root, "repo");
        await DevelopmentSyntheticSolutionRepository.CreateAsync(repository, includeTests: false).ConfigureAwait(false);
        await WriteAndCommitAsync(repository, DevelopmentSyntheticSolutionRepository.PassingLibrarySource, "implement the feature").ConfigureAwait(false);

        await using var harness = DevWorkflowHarness.WithARealSandbox(("Development:Enabled", "true"));
        var projectId = await CreateProjectAsync(harness, repository).ConfigureAwait(false);

        var passing = await harness.StartRunAsync(BuildOnlyToolGraph, "Build the solution.", projectId).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(passing).ConfigureAwait(false);

        var built = await harness.ReadNodeRunAsync(passing, "build").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, built.Status, AssertEx.NotNull(built.TerminalReason ?? built.OutputJson));
        AssertEx.Equal(DevWorkflowRunStatus.Completed, (await harness.ReadRunAsync(passing).ConfigureAwait(false)).Status);
        AssertEx.Contains(AssertEx.NotNull(built.OutputJson), "\"passed\":true");
        AssertEx.Contains(AssertEx.NotNull(built.OutputJson), "\"commandsRun\":3", message: "the node's own command list is the one that ran.");

        var artifact = (await harness.ReadArtifactsAsync(passing).ConfigureAwait(false)).Single();
        AssertEx.Equal(DevWorkflowArtifactKind.ValidationReport, artifact.Kind);
        var report = await harness.ReadArtifactTextAsync(passing, artifact).ConfigureAwait(false);
        AssertEx.Contains(report, "dotnet_build_release_no_restore");
        AssertEx.Contains(report, "[REDACTED:development-path]", message: "a build prints its output path, and the report must not carry this host's layout.");
        AssertEx.False(report.Contains(_root, StringComparison.OrdinalIgnoreCase), "no absolute host path survives into a stored report.");

        // The same repository, one commit later, no longer compiles. Nothing about the workflow changes: the same node
        // runs the same commands and the deterministic gate says no.
        await WriteAndCommitAsync(repository, DevelopmentSyntheticSolutionRepository.BuildBreakingLibrarySource, "break the build").ConfigureAwait(false);

        var failing = await harness.StartRunAsync(BuildOnlyToolGraph, "Build the solution again.", projectId).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(failing).ConfigureAwait(false);

        var broken = await harness.ReadNodeRunAsync(failing, "build").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, broken.Status, "the node allows one attempt, so a retryable failure has nowhere left to go but a human.");
        AssertEx.Equal(expected: 1, broken.Attempt, "an exhausted node run spends no further attempt on its way to being blocked.");
        AssertEx.Equal("ToolCommandFailed", broken.FailureClass, "a build that does not compile is a verdict, not an engine fault.");
        AssertEx.Contains(AssertEx.NotNull(broken.OutputJson), "\"failureCode\":\"command_failed\"");
        AssertEx.Contains(await harness.ReadArtifactTextAsync(failing,
                    (await harness.ReadArtifactsAsync(failing).ConfigureAwait(false)).Single())
                .ConfigureAwait(false),
            "CS1002",
            message: "the report names the compiler error, which is the whole point of keeping it.");
    }

    /// <summary>
    ///     A real pass that runs out of time keeps what it had gathered. The commands it never reached are the reason
    ///     the report says no, and the report exists at all — which is the whole difference from the answer this used to
    ///     give, where a timed-out node run left an operator one sentence and no evidence.
    ///     <para>
    ///         Deliberately no assertion that a particular command finished inside the budget: how many of them do is a
    ///         property of the machine, and the claim under test is that whatever DID finish is written down.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARealBuildThatRunsOutOfTimeKeepsTheEvidenceItGathered()
    {
        var repository = Path.Combine(_root, "repo");
        await DevelopmentSyntheticSolutionRepository.CreateAsync(repository, includeTests: true).ConfigureAwait(false);
        await WriteAndCommitAsync(repository, DevelopmentSyntheticSolutionRepository.PassingLibrarySource, "implement the feature").ConfigureAwait(false);

        await using var harness = DevWorkflowHarness.WithARealSandbox(("Development:Enabled", "true"));
        var projectId = await CreateProjectAsync(harness, repository).ConfigureAwait(false);

        var runId = await harness.StartRunAsync(ImpatientBuildGraph, "Build the solution, quickly.", projectId).ConfigureAwait(false);
        await harness.AdvanceThroughToolLaneAsync(runId).ConfigureAwait(false);

        var timedOut = await harness.ReadNodeRunAsync(runId, "build").ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, timedOut.Status, AssertEx.NotNull(timedOut.TerminalReason ?? timedOut.OutputJson));
        AssertEx.Equal(DevWorkflowFailureClasses.Timeout, timedOut.FailureClass, "the clock ended it, which is a different fact from the build's verdict.");
        AssertEx.Contains(AssertEx.NotNull(timedOut.TerminalReason), "1 seconds");
        AssertEx.Contains(AssertEx.NotNull(timedOut.OutputJson), "\"passed\":false");
        AssertEx.False(AssertEx.NotNull(timedOut.OutputJson).Contains("\"commandsRun\":4", StringComparison.Ordinal),
            "a pass that ran out of time cannot have finished every command it was given.");

        var artifact = (await harness.ReadArtifactsAsync(runId).ConfigureAwait(false)).Single();
        AssertEx.Equal(DevWorkflowArtifactKind.ValidationReport, artifact.Kind);
        var report = await harness.ReadArtifactTextAsync(runId, artifact).ConfigureAwait(false);
        AssertEx.Contains(report, "\"passed\":false");
        AssertEx.Contains(report, DevelopmentCommandProfileCatalog.DotnetSlnx, message: "the report still names the profile and commit its evidence was gathered against.");
        AssertEx.False(report.Contains(_root, StringComparison.OrdinalIgnoreCase), "no absolute host path survives into a stored report.");
    }

    /// <summary>
    ///     Registers the repository and creates the Development project the tool node runs against, with the .NET
    ///     solution profile bound to the synthetic fixture's own solution file.
    /// </summary>
    private static async Task<Guid> CreateProjectAsync(DevWorkflowHarness harness, string repository)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var bindings = scope.ServiceProvider.GetRequiredService<IDevelopmentRepositoryBindingService>();
        var reference = await bindings.RegisterAsync("dev-workflow-tool-fixture", repository).ConfigureAwait(false);
        var profile = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx,
            DevelopmentSyntheticSolutionRepository.SolutionPath);

        var projectId = Guid.NewGuid();
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>()
                       .CreateProjectAsync(new DevelopmentCreateProjectCommand(projectId,
                           Guid.NewGuid(),
                           Guid.NewGuid(),
                           "Keep the solution building.",
                           Guid.Parse(reference.Id),
                           DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)),
                           "main",
                           "Build",
                           "Build the solution.",
                           "[]",
                           DevelopmentEgressPolicy.LocalOnly,
                           CoderModelId: null,
                           ReviewerModelId: null,
                           MaxReviewRounds: 3,
                           ConfigurationVersion: 1,
                           TrustedRepositoryAcknowledged: true,
                           DevelopmentTrustPolicy.CurrentVersion,
                           DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                           MaxTokens: 2048,
                           MaxDurationSeconds: 600,
                           Encoding.UTF8.GetString(profile.ToCanonicalUtf8())))
                       .ConfigureAwait(false);
        return projectId;
    }

    private static async Task WriteAndCommitAsync(string repository, string librarySource, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repository, DevelopmentSyntheticSolutionRepository.LibrarySourcePath.Replace('/', Path.DirectorySeparatorChar)),
                      librarySource)
                  .ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "add", "-A", "--", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "commit", "-m", message).ConfigureAwait(false);
    }
}
