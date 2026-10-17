namespace XE_Local_AI_Engine.Tests.DevWorkflows.Execution;

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     An Agent node whose agent reads the repository with the coder tools gets its work item's Development repository
///     to read, and one that has none to read blocks before a session exists, saying which tools and what Retry and Skip do.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DevWorkflowRepositoryResearchTests : IDisposable
{
    private readonly string _repository = Directory.CreateTempSubdirectory("xe-devwf-repository-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_repository))
        {
            Directory.Delete(_repository, recursive: true);
        }
    }

    [Test]
    public async Task ANodeReadingTheRepository_DrivesItsSessionOnTheProjectsRepository()
    {
        // Private host: it registers a repository and a project of its own.
        await using var harness = new DevWorkflowHarness();
        var agentId = await RepositoryReaderAsync(harness);
        var projectId = await SeedGitProjectAsync(harness);
        var runId = await harness.StartRunAsync(BoundAgent(agentId), developmentProjectId: projectId);

        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Contains(harness.Agent.Runtimes,
            entry => string.Equals(entry.Runtime?.RepositoryRoot, DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(_repository), StringComparison.Ordinal),
            "the coder reads of this session must serve the work item's repository, or the research has nothing to read.");
    }

    [Test]
    public async Task ANodeReadingTheRepositoryWithNoProject_BlocksNamingTheToolsAndWhatEachAnswerDoes()
    {
        // Private host: the shared one's fake session lists accumulate every sibling's sessions.
        await using var harness = new DevWorkflowHarness();
        var agentId = await RepositoryReaderAsync(harness);
        var runId = await harness.StartRunAsync(BoundAgent(agentId));

        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        var research = await harness.ReadNodeRunAsync(runId, "research");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, research.Status);
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, research.FailureClass);
        var reason = AssertEx.NotNull(research.TerminalReason);
        foreach (var tool in DevWorkflowAgentExecutor.RepositoryToolNames)
        {
            AssertEx.Contains(reason, tool, message: "the reason names what the agent could not be given.");
        }

        AssertEx.Contains(reason, "Retry");
        AssertEx.Contains(reason, "Skip", message: "and what the two answers it is offered will do.");
        AssertEx.Empty(harness.Agent.Created, "refused before a session exists, so there is nothing to release.");
    }

    /// <summary>A registered repository whose folder is gone blocks the node for a person, not an exception every sweep.</summary>
    [Test]
    public async Task ANodeWhoseRepositoryFolderIsGone_BlocksAsConfiguration()
    {
        await using var harness = new DevWorkflowHarness();
        var agentId = await RepositoryReaderAsync(harness);
        var projectId = await SeedGitProjectAsync(harness);
        Directory.Delete(_repository, recursive: true);
        var runId = await harness.StartRunAsync(BoundAgent(agentId), developmentProjectId: projectId);

        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        var research = await harness.ReadNodeRunAsync(runId, "research");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, research.Status, $"{research.FailureClass} — {research.TerminalReason}");
        AssertEx.Equal(DevWorkflowFailureClasses.Configuration, research.FailureClass);
        AssertEx.Contains(AssertEx.NotNull(research.TerminalReason), "cannot be resolved");
        AssertEx.False(research.TerminalReason!.Contains(_repository, StringComparison.Ordinal), "the reason never names the host path.");
        AssertEx.Empty(harness.Agent.Created);
    }

    [Test]
    public async Task ANodeWhoseAgentDoesNotReadTheRepository_IsGivenNoRoot()
    {
        await using var harness = new DevWorkflowHarness();
        var agentId = await AllowingAsync(harness, "record_finding");
        var projectId = await SeedGitProjectAsync(harness);
        var runId = await harness.StartRunAsync(BoundAgent(agentId), developmentProjectId: projectId);

        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.NotEmpty(harness.Agent.Runtimes);
        AssertEx.False(harness.Agent.Runtimes.Any(entry => entry.Runtime?.RepositoryRoot is not null), "a plan or verify node never resolves a repository it cannot read.");
    }

    private static string BoundAgent(Guid agentDefinitionId) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [{ "nodeKey": "research", "nodeType": "Agent", "label": "Research", "agentDefinitionId": "{{agentDefinitionId}}" }],
            "edges": []
          }
          """;

    private static Task<Guid> RepositoryReaderAsync(DevWorkflowHarness harness) =>
        AllowingAsync(harness, [.. DevWorkflowAgentExecutor.RepositoryToolNames, "record_finding"]);

    private static async Task<Guid> AllowingAsync(DevWorkflowHarness harness, params string[] toolNames)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>()
                                 .AddAsync(new AgentDefinitionInput
                                 {
                                     Name = "Repository reader",
                                     Description = null,
                                     Instructions = "Read the repository.",
                                     ModelProfile = null,
                                     ReasoningEffort = null,
                                     Kind = AgentDefinitionKind.Single,
                                     AllowedToolNames = toolNames,
                                     ToolApprovals = new Dictionary<string, bool>(StringComparer.Ordinal),
                                     OrchestrationTopologyJson = null
                                 });
        return created.Id;
    }

    /// <summary>A real git repository registered and bound the way the Development page does it, so the binding resolves for real.</summary>
    private async Task<Guid> SeedGitProjectAsync(DevWorkflowHarness harness)
    {
        using (var git = Process.Start(new ProcessStartInfo("git", ["init", "--quiet", "--initial-branch=main", "."])
               {
                   WorkingDirectory = _repository,
                   UseShellExecute = false
               })!)
        {
            await git.WaitForExitAsync();
            AssertEx.Equal(expected: 0, git.ExitCode, "the test repository could not be created.");
        }

        await using var scope = harness.Services.CreateAsyncScope();
        var folder = await scope.ServiceProvider.GetRequiredService<IDevelopmentRepositoryBindingService>().RegisterAsync($"repo-{Guid.NewGuid():N}"[..20], _repository);
        var projectId = Guid.NewGuid();
        _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>()
                       .CreateProjectAsync(new DevelopmentCreateProjectCommand
                       {
                           ProjectId = projectId,
                           TaskId = Guid.NewGuid(),
                           OperationId = Guid.NewGuid(),
                           Objective = "Keep the product working.",
                           SelectedFolderId = Guid.Parse(folder.Id),
                           RepositoryIdentityHash = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(_repository)),
                           BaseBranch = "main",
                           Title = "Add the feature",
                           Requirements = "It has to do the thing.",
                           AcceptanceCriteriaJson = "[\"it does the thing\"]"
                       });
        return projectId;
    }
}
