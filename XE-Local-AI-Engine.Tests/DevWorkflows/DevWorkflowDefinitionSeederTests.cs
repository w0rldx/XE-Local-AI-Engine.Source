namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What the shipped <c>feature-development-v1</c> template says, and what its revision list has to keep saying.
///     <para>
///         Wording pinned by string assertion rather than by execution: there is no code path that fails when the
///         decomposition instructions stop naming the constraints a coder is actually held to, which is precisely why
///         they went four live runs without them. The revision assertions are the other half, and they are about the
///         RAW constants rather than the parsed graphs: catch-up is keyed on a SHA-256 of the graph bytes, so a kept
///         revision that drifted by a space, or that was written but never passed to the seeder, stops every untouched
///         installation from ever catching up again — logged at information level and carried on from.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DevWorkflowDefinitionSeederTests
{
    /// <summary>
    ///     The template ships valid: it is parsed here through the same validator a run start uses, so a graph that
    ///     would fail at run start fails in this suite instead of at a customer's startup.
    /// </summary>
    [Test]
    public void TheShippedFeatureTemplateParsesThroughTheContractARunStartUses()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph);

        AssertEx.Equal(graph.Nodes.Count,
            DevWorkflowGraphContract.ValidateAndCountNodes(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph, new DevWorkflowOptions().MaxNodesPerDefinition),
            "the shipped template has to fit under the cap a node ships with, or a fresh install could not save what it seeded.");
        AssertEx.NotNull(graph.Nodes["decompose"].Materialization);
        AssertEx.Equal(DevWorkflowNodeType.DevTask,
            graph.Nodes[graph.Nodes["decompose"].Materialization!.TemplateNodeKey].NodeType,
            "the decomposition's template root is a DevTask, which is what makes the package's 'changes' rule apply to it.");
    }

    /// <summary>
    ///     The three facts the decomposition has to state, because nothing downstream will: a task names the files it
    ///     changes, a task that changes nothing is not a task, and tests go in a file that does not exist yet.
    /// </summary>
    [Test]
    public void TheShippedDecompositionTellsTheModelWhatASliceHasToBe()
    {
        var instructions = AssertEx.NotNull(DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph).Nodes["decompose"].Instructions);

        AssertEx.Contains(instructions, "\"changes\"", message: "the schema the model is shown carries the field the materializer now requires of it.");
        AssertEx.Contains(instructions, "NEW test file", message: "an existing test file cannot be edited, so a slice that adds tests has to say where they go.");
        AssertEx.Contains(instructions, "Every task must change code");
        AssertEx.Contains(instructions, "Default to ONE task", message: "the live failure was a one-method request split four ways.");
        AssertEx.Contains(instructions,
            "kind \"Report\"",
            message: "the save_artifact contract is unchanged: a work session has no word for a task package, and the promotion is what makes it one.");
    }

    /// <summary>
    ///     The shipped research node binds the repository-research persona, and that persona is offered every repository read
    ///     the agent lane serves from the work item's project: the knowledge-base persona it used to bind could read nothing there.
    /// </summary>
    [Test]
    public void TheShippedResearchNodeBindsAPersonaThatReadsTheRepository()
    {
        var research = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph).Nodes["research"];
        var persona = WorkSessionAgentSeeder.BuildRepositoryResearchSeedInput();

        AssertEx.Equal(AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug, research.AgentSeedSlug);
        foreach (var tool in DevWorkflowAgentExecutor.RepositoryToolNames)
        {
            AssertEx.Contains(persona.AllowedToolNames, tool);
        }

        AssertEx.False(WorkSessionAgentSeeder.BuildResearchSeedInput().AllowedToolNames.Any(DevWorkflowAgentExecutor.RepositoryToolNames.Contains),
            "the knowledge-base persona other features bind is left as it was; the template got its own.");
    }

    /// <summary>
    ///     The revision this build replaces differs from the shipped graph only in which persona its research node binds, compared
    ///     by structure and by every node's text so any other drift in the kept copy fails here.
    /// </summary>
    [Test]
    public void TheNewestKeptRevisionDiffersFromTheShippedTemplateOnlyInTheResearchPersona()
    {
        var shipped = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph);
        var kept = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision3);

        AssertEx.Equal(string.Join(",", shipped.Edges.Select(static edge => edge.ToString()).Order(StringComparer.Ordinal)),
            string.Join(",", kept.Edges.Select(static edge => edge.ToString()).Order(StringComparer.Ordinal)));
        foreach (var (key, node) in shipped.Nodes)
        {
            AssertEx.Equal(node.Instructions ?? string.Empty, kept.Nodes[key].Instructions ?? string.Empty, $"node '{key}' was not the one this change was about.");
            AssertEx.Equal(key == "research" ? AgentDefaults.WorkSessionResearchAgentSeedSlug : node.AgentSeedSlug ?? string.Empty, kept.Nodes[key].AgentSeedSlug ?? string.Empty);
        }
    }

    /// <summary>
    ///     Revision 2 is the graph revision 3 replaced, and nothing else. Compared by structure and by every node's text, so the
    ///     assertion is "only the decomposition's instructions moved" rather than "the two strings differ", which any edit would satisfy.
    /// </summary>
    [Test]
    public void TheKeptRevisionDiffersFromTheShippedTemplateOnlyInWhatTheDecompositionIsTold()
    {
        var shipped = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision3);
        var kept = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision2);

        AssertEx.Equal(string.Join(",", shipped.Nodes.Keys.Order(StringComparer.Ordinal)), string.Join(",", kept.Nodes.Keys.Order(StringComparer.Ordinal)));
        AssertEx.Equal(string.Join(",", shipped.Edges.Select(static edge => edge.ToString()).Order(StringComparer.Ordinal)),
            string.Join(",", kept.Edges.Select(static edge => edge.ToString()).Order(StringComparer.Ordinal)));

        foreach (var (key, node) in shipped.Nodes.Where(static entry => entry.Key != "decompose"))
        {
            AssertEx.Equal(node.Instructions ?? string.Empty, kept.Nodes[key].Instructions ?? string.Empty, $"node '{key}' was not the one this change was about.");
        }

        AssertEx.False(string.Equals(kept.Nodes["decompose"].Instructions, shipped.Nodes["decompose"].Instructions, StringComparison.Ordinal),
            "the decomposition IS what changed, so keeping the old text is the whole point of the revision.");
        AssertEx.False(AssertEx.NotNull(kept.Nodes["decompose"].Instructions).Contains("\"changes\"", StringComparison.Ordinal),
            "and the kept copy is the OLD text, not a second copy of the new one — an installation on it would otherwise never be recognised.");
    }

    /// <summary>
    ///     The kept revision is in the list the seeder is actually given, and it hashes to something other than the
    ///     current graph. Both are about the RAW constant, which is what the catch-up keys on: a copy that reads
    ///     correctly but drifted by one space, or one that was never passed to <c>SeedAsync</c>, leaves every
    ///     installation holding it unrecognised and stranded on it for good.
    /// </summary>
    [Test]
    public void TheKeptRevisionIsInTheListTheSeederIsGivenAndHashesApartFromTheShippedGraph()
    {
        AssertEx.Contains(DevWorkflowDefinitionSeeder.FeatureDevelopmentPriorRevisions,
            revision => string.Equals(revision, DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision3, StringComparison.Ordinal),
            "the revision this build replaces has to reach the seeder, not merely exist beside it.");

        AssertEx.False(string.Equals(DevWorkflowDefinitionSeeder.GraphHash(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision3),
                DevWorkflowDefinitionSeeder.GraphHash(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph),
                StringComparison.Ordinal),
            "a revision that hashes to the current graph is a copy of it, which catches nobody up and hides the edit it was meant to preserve.");

        AssertEx.Equal(DevWorkflowDefinitionSeeder.FeatureDevelopmentPriorRevisions.Length,
            DevWorkflowDefinitionSeeder.FeatureDevelopmentPriorRevisions.Select(DevWorkflowDefinitionSeeder.GraphHash).Distinct(StringComparer.Ordinal).Count(),
            "and no two revisions are the same graph, which would mean one of them was copied from the wrong place.");
    }
}
