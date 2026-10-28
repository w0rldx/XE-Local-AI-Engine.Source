namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What the shipped templates say, and what the <c>feature-development-v1</c> revision list has to keep saying.
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
    ///     the agent lane serves from the work item's project: a knowledge-base persona could read nothing there.
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

    /// <summary>Revision 4 is revision 3 with the research node bound to the repository-research persona, and nothing else.</summary>
    /// <remarks>Compared by structure and by every node's text, so any other drift in the kept copy fails here.</remarks>
    [Test]
    public void TheFourthRevisionDiffersFromTheThirdOnlyInTheResearchPersona()
    {
        var newer = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision4);
        var kept = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision3);

        AssertEx.Equal(string.Join(",", newer.Edges.Select(static edge => edge.ToString()).Order(StringComparer.Ordinal)),
            string.Join(",", kept.Edges.Select(static edge => edge.ToString()).Order(StringComparer.Ordinal)));
        foreach (var (key, node) in newer.Nodes)
        {
            AssertEx.Equal(node.Instructions ?? string.Empty, kept.Nodes[key].Instructions ?? string.Empty, $"node '{key}' was not the one this change was about.");
            AssertEx.Equal(key == "research" ? AgentDefaults.WorkSessionResearchAgentSeedSlug : node.AgentSeedSlug ?? string.Empty, kept.Nodes[key].AgentSeedSlug ?? string.Empty);
        }
    }

    /// <summary>The previous kept revision is the newest one with only the verification node's instructions and output contract changed.</summary>
    /// <remarks>
    ///     Every node compared whole on every field but those, and every edge with its condition, so a kept copy that drifted anywhere
    ///     else, or a verify edit that leaked into another node, fails here.
    /// </remarks>
    [Test]
    [Arguments(DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug)]
    [Arguments(DevWorkflowDefinitionSeeder.DevIntakeSlug)]
    public void ThePreviousKeptRevisionDiffersFromTheNewestOnlyInWhatVerifyIsToldAndAnswers(string slug)
    {
        var template = TemplateFor(slug);
        var kept = AssertOnlyVerifyDiffers(DevWorkflowGraph.Parse(template.NewestRevision), DevWorkflowGraph.Parse(template.PreviousRevision));

        AssertEx.Null(kept.Nodes["verify"].OutputSchema, "the kept copy is the one an installation seeded before verify had a verdict to hand in.");
        AssertEx.False(AssertEx.NotNull(kept.Nodes["verify"].Instructions).Contains("refusedTasks", StringComparison.Ordinal),
            "and its text is the OLD one, which never named the verdict.");
    }

    /// <summary>The revision before that is the previous kept one with only the verification node's instructions changed.</summary>
    [Test]
    [Arguments(DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug)]
    [Arguments(DevWorkflowDefinitionSeeder.DevIntakeSlug)]
    public void TheOlderKeptRevisionDiffersFromThePreviousOnlyInWhatVerifyIsTold(string slug)
    {
        var template = TemplateFor(slug);
        var newer = DevWorkflowGraph.Parse(template.PreviousRevision);
        var kept = AssertOnlyVerifyDiffers(newer, DevWorkflowGraph.Parse(template.OlderRevision));

        AssertEx.Equal(ShapeWithoutInstructions(kept.Nodes["verify"]), ShapeWithoutInstructions(newer.Nodes["verify"]), "only the verify text changed between these two.");
        AssertEx.False(AssertEx.NotNull(kept.Nodes["verify"].Instructions).Contains("read_upstream_artifact", StringComparison.Ordinal),
            "and the older copy is the text from before verify was handed the patch.");
    }

    /// <summary>The newest kept revision is the shipped graph with only what the plan, verify and (intake) needsinfo nodes are told changed.</summary>
    /// <remarks>Every other node compared whole, and every edge with its condition, so a kept copy that drifted elsewhere fails here.</remarks>
    [Test]
    [Arguments(DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug)]
    [Arguments(DevWorkflowDefinitionSeeder.DevIntakeSlug)]
    public void TheNewestKeptRevisionDiffersFromTheShippedTemplateOnlyInWhatItsAgentsAreTold(string slug)
    {
        var template = TemplateFor(slug);
        var shipped = DevWorkflowGraph.Parse(template.Graph);
        var kept = DevWorkflowGraph.Parse(template.NewestRevision);
        string[] told = slug == DevWorkflowDefinitionSeeder.DevIntakeSlug ? ["plan", "fixplan", "verify", "needsinfo"] : ["plan", "verify"];

        AssertEx.Equal(string.Join(",", shipped.Nodes.Keys.Order(StringComparer.Ordinal)), string.Join(",", kept.Nodes.Keys.Order(StringComparer.Ordinal)));
        AssertEx.Equal(string.Join("\n", shipped.Edges.Select(static edge => JsonSerializer.Serialize(edge)).Order(StringComparer.Ordinal)),
            string.Join("\n", kept.Edges.Select(static edge => JsonSerializer.Serialize(edge)).Order(StringComparer.Ordinal)),
            "edges and their conditions are untouched.");
        foreach (var (key, node) in shipped.Nodes)
        {
            AssertEx.Equal(ShapeWithoutInstructions(kept.Nodes[key]), ShapeWithoutInstructions(node), $"node '{key}' keeps every field but its instructions.");
            AssertEx.Equal(!told.Contains(key),
                string.Equals(kept.Nodes[key].Instructions, node.Instructions, StringComparison.Ordinal),
                $"node '{key}' changed its instructions exactly when it is one this change was about.");
        }

        AssertEx.False(AssertEx.NotNull(kept.Nodes["plan"].Instructions).Contains("Deviations from the request", StringComparison.Ordinal),
            "and the kept copy is the text from before the plan was held to the request.");
    }

    /// <summary>
    ///     Both plans keep what the request names and list any departure, and both verifications judge the request before the plan:
    ///     without it a plan that renamed the requested interface is approved, implemented and signed off against itself.
    /// </summary>
    /// <remarks>Pinned by wording because nothing fails in code when a model is told to judge a patch against the wrong document.</remarks>
    [Test]
    [Arguments(DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug)]
    [Arguments(DevWorkflowDefinitionSeeder.DevIntakeSlug)]
    public void TheShippedPlanAndVerificationAreHeldToTheRequest(string slug)
    {
        var graph = DevWorkflowGraph.Parse(TemplateFor(slug).Graph);
        string[] plans = slug == DevWorkflowDefinitionSeeder.DevIntakeSlug ? ["plan", "fixplan"] : ["plan"];

        foreach (var key in plans)
        {
            var plan = AssertEx.NotNull(graph.Nodes[key].Instructions);
            AssertEx.Contains(plan, "keep every name, signature and interface it specifies", message: $"'{key}' keeps what the request names.");
            AssertEx.Contains(plan, "Deviations from the request", message: $"'{key}' lists every departure under one heading the operator can find.");
            AssertEx.Contains(plan, "workItemRequest", message: "the plan is told where the request is in its objective.");
        }

        var verify = AssertEx.NotNull(graph.Nodes["verify"].Instructions);
        AssertEx.Contains(verify, "against the operator's request first");
        AssertEx.True(verify.IndexOf("request first", StringComparison.Ordinal) < verify.IndexOf("then against the approved plan", StringComparison.Ordinal),
            "the request is judged before the plan.");
        AssertEx.Contains(verify, "even when the patch matches the plan", message: "a patch true to the plan but not the request is refused.");
        AssertEx.Contains(verify, "verdict \"refused\" with refusedTasks", message: "the verdict contract is unchanged.");
    }

    /// <summary>
    ///     Asserts <paramref name="kept" /> is <paramref name="shipped" /> with only the verify node's instructions and output contract
    ///     changed, and that its instructions did change; answers <paramref name="kept" />.
    /// </summary>
    private static DevWorkflowGraph AssertOnlyVerifyDiffers(DevWorkflowGraph shipped, DevWorkflowGraph kept)
    {
        AssertEx.Equal(string.Join(",", shipped.Nodes.Keys.Order(StringComparer.Ordinal)), string.Join(",", kept.Nodes.Keys.Order(StringComparer.Ordinal)));
        AssertEx.Equal(string.Join("\n", shipped.Edges.Select(static edge => JsonSerializer.Serialize(edge)).Order(StringComparer.Ordinal)),
            string.Join("\n", kept.Edges.Select(static edge => JsonSerializer.Serialize(edge)).Order(StringComparer.Ordinal)),
            "edges and their conditions are untouched.");
        foreach (var (key, node) in shipped.Nodes)
        {
            var verify = key == "verify";
            AssertEx.Equal(ShapeWithoutInstructions(kept.Nodes[key], verify), ShapeWithoutInstructions(node, verify), $"node '{key}' keeps every field but what this change was about.");
            if (!verify)
            {
                AssertEx.Equal(kept.Nodes[key].Instructions ?? string.Empty, node.Instructions ?? string.Empty, $"node '{key}' was not the one this change was about.");
            }
        }

        AssertEx.False(string.Equals(kept.Nodes["verify"].Instructions, shipped.Nodes["verify"].Instructions, StringComparison.Ordinal),
            "the verification instructions ARE what changed.");
        return kept;
    }

    /// <summary>Both verification nodes are told the patch reaches them, how to read past the inlined part, and what to do when it never arrived.</summary>
    /// <remarks>
    ///     Pinned by wording because no code path fails when a model is told to sign off on a diff it cannot see, which is
    ///     what blocked every feature and bug run before the patch was published.
    /// </remarks>
    [Test]
    [Arguments(DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug)]
    [Arguments(DevWorkflowDefinitionSeeder.DevIntakeSlug)]
    public void TheShippedVerificationReadsEveryPatchBeforeItSignsOff(string slug)
    {
        var instructions = AssertEx.NotNull(DevWorkflowGraph.Parse(TemplateFor(slug).Graph).Nodes["verify"].Instructions);

        AssertEx.Contains(instructions, "validate#<task id>.patch", message: "the artifact a materialized validation clone publishes is named by its clone key.");
        AssertEx.Contains(instructions, "read_upstream_artifact", message: "a patch past the inline share is read with the tool, not guessed at.");
        AssertEx.Contains(instructions, "nextOffset", message: "the tool pages, so the instruction names the cursor it hands back.");
        AssertEx.Contains(instructions, "until it answers the end of the artifact", message: "the engine blocks a patch not read to its end, so the model is told so.");
        AssertEx.Contains(instructions, "was not published", message: "a patch over the artifact cap never arrives, and the report says why.");
        AssertEx.Contains(instructions, "verdict \"refused\" with refusedTasks", message: "a refused task is handed in as the verdict the engine blocks on.");
        AssertEx.Contains(instructions, "plan.md");
        AssertEx.Contains(instructions, "name \"verification.md\"", message: "the save_artifact contract is unchanged.");
        if (slug == DevWorkflowDefinitionSeeder.DevIntakeSlug)
        {
            AssertEx.Contains(instructions, "fix-plan.md", message: "a bug run's approved plan is the fix plan.");
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
    [Arguments(DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug)]
    [Arguments(DevWorkflowDefinitionSeeder.DevIntakeSlug)]
    public void TheKeptRevisionIsInTheListTheSeederIsGivenAndHashesApartFromTheShippedGraph(string slug)
    {
        var template = TemplateFor(slug);
        var newest = template.NewestRevision;
        var revisions = template.PriorRevisions;

        AssertEx.Contains(revisions,
            revision => string.Equals(revision, newest, StringComparison.Ordinal),
            "the revision this build replaces has to reach the seeder, not merely exist beside it.");

        AssertEx.False(string.Equals(DevWorkflowDefinitionSeeder.GraphHash(newest), DevWorkflowDefinitionSeeder.GraphHash(template.Graph), StringComparison.Ordinal),
            "a revision that hashes to the current graph is a copy of it, which catches nobody up and hides the edit it was meant to preserve.");

        AssertEx.Equal(revisions.Length,
            revisions.Select(DevWorkflowDefinitionSeeder.GraphHash).Distinct(StringComparer.Ordinal).Count(),
            "and no two revisions are the same graph, which would mean one of them was copied from the wrong place.");
    }

    /// <summary>
    ///     An untouched row holding the graph the previous build shipped is rewritten to the one this build ships, through
    ///     the definition update, and nothing else is written: the slug's other templates already match.
    /// </summary>
    [Test]
    [Arguments(DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug)]
    [Arguments(DevWorkflowDefinitionSeeder.DevIntakeSlug)]
    public async Task AnUntouchedRowOnThePreviousRevisionIsUpgradedToTheShippedTemplate(string slug)
    {
        var template = TemplateFor(slug);
        var store = StoreHolding(slug, template.NewestRevision);

        await SeedAsync(store);

        var updates = store.ReceivedCalls().Where(static call => call.GetMethodInfo().Name == nameof(IDevWorkflowStore.UpdateDefinitionAsync))
                           .Select(static call => (UpdateDevWorkflowDefinitionCommand)call.GetArguments()[0]!)
                           .ToList();
        AssertEx.Equal(expected: 1, updates.Count, "one row was on a revision this build knows it shipped.");
        var update = updates[0];
        AssertEx.Equal(template.Graph, update.GraphJson, "the row now holds the shipped template, byte for byte.");
        AssertEx.Null(update.Name, "the name is the operator's to keep.");
        AssertEx.Equal(expected: 1, update.ExpectedVersion);
        _ = await store.DidNotReceiveWithAnyArgs().CreateDefinitionAsync(default!, default);
    }

    /// <summary>A row whose graph matches neither the shipped template nor any kept revision is an operator's edit, and is left alone.</summary>
    [Test]
    [Arguments(DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug)]
    [Arguments(DevWorkflowDefinitionSeeder.DevIntakeSlug)]
    public async Task AnOperatorEditedRowIsNeverRewritten(string slug)
    {
        const string Edited = """
                              {
                                "schemaVersion": 1,
                                "nodes": [{ "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve" }],
                                "edges": []
                              }
                              """;
        var store = StoreHolding(slug, Edited);

        await SeedAsync(store);

        _ = await store.DidNotReceiveWithAnyArgs().UpdateDefinitionAsync(default!, default);
        _ = await store.DidNotReceiveWithAnyArgs().CreateDefinitionAsync(default!, default);
    }

    /// <summary>
    ///     A store listing one seeded row per shipped slug: <paramref name="slug" /> holding <paramref name="graphJson" />,
    ///     the others already on their shipped template, so only the row under test can draw a write.
    /// </summary>
    private static IDevWorkflowStore StoreHolding(string slug, string graphJson)
    {
        var shipped = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DevWorkflowDefinitionSeeder.ResearchPlanApprovalSlug] = DevWorkflowDefinitionSeeder.ResearchPlanApprovalGraph,
            [DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug] = DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph,
            [DevWorkflowDefinitionSeeder.DevIntakeSlug] = DevWorkflowDefinitionSeeder.DevIntakeGraph,
            [slug] = graphJson
        };
        var rows = shipped.Select(static entry => new DevWorkflowDefinitionSummary
                          {
                              Id = Guid.NewGuid(),
                              Name = entry.Key,
                              GraphHash = DevWorkflowDefinitionSeeder.GraphHash(entry.Value),
                              NodeCount = 1,
                              Source = DevWorkflowDefinitionSource.Seeded,
                              SeedSlug = entry.Key,
                              Archived = false,
                              Version = 1,
                              CreatedAtUtc = 0,
                              UpdatedAtUtc = 0
                          })
                          .ToList();

        var store = Substitute.For<IDevWorkflowStore>();
        store.ListDefinitionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(rows);
        store.UpdateDefinitionAsync(Arg.Any<UpdateDevWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(static call =>
             {
                 var command = call.Arg<UpdateDevWorkflowDefinitionCommand>();
                 return new DevWorkflowDefinitionSnapshot
                 {
                     Id = command.DefinitionId,
                     Name = "updated",
                     GraphJson = AssertEx.NotNull(command.GraphJson),
                     GraphHash = DevWorkflowDefinitionSeeder.GraphHash(command.GraphJson!),
                     NodeCount = command.NodeCount ?? 0,
                     Source = DevWorkflowDefinitionSource.Seeded,
                     SeedSlug = null,
                     Archived = false,
                     Version = command.ExpectedVersion + 1,
                     CreatedAtUtc = 0,
                     UpdatedAtUtc = 0
                 };
             });
        return store;
    }

    private static Task SeedAsync(IDevWorkflowStore store)
    {
        var settings = Substitute.For<INodeRuntimeSettings>();
        settings.GetDevWorkflowsEnabledAsync(Arg.Any<CancellationToken>()).Returns(true);
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        return new DevWorkflowDefinitionSeeder(services.GetRequiredService<IServiceScopeFactory>(), settings, NullLogger<DevWorkflowDefinitionSeeder>.Instance)
            .StartAsync(CancellationToken.None);
    }

    /// <summary>What one seeded slug ships: its live graph, the revision it replaces, and the list the seeder is given.</summary>
    private sealed class ShippedTemplate
    {
        public required string Graph { get; init; }

        public required string NewestRevision { get; init; }

        /// <summary>The revision <see cref="NewestRevision" /> replaced.</summary>
        public required string PreviousRevision { get; init; }

        /// <summary>The revision <see cref="PreviousRevision" /> replaced.</summary>
        public required string OlderRevision { get; init; }

        public required string[] PriorRevisions { get; init; }
    }

    private static ShippedTemplate TemplateFor(string slug) =>
        slug == DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug
            ? new ShippedTemplate
            {
                Graph = DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph,
                NewestRevision = DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision6,
                PreviousRevision = DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision5,
                OlderRevision = DevWorkflowDefinitionSeeder.FeatureDevelopmentGraphRevision4,
                PriorRevisions = DevWorkflowDefinitionSeeder.FeatureDevelopmentPriorRevisions
            }
            : new ShippedTemplate
            {
                Graph = DevWorkflowDefinitionSeeder.DevIntakeGraph,
                NewestRevision = DevWorkflowDefinitionSeeder.DevIntakeGraphRevision3,
                PreviousRevision = DevWorkflowDefinitionSeeder.DevIntakeGraphRevision2,
                OlderRevision = DevWorkflowDefinitionSeeder.DevIntakeGraphRevision1,
                PriorRevisions = DevWorkflowDefinitionSeeder.DevIntakePriorRevisions
            };

    /// <summary>A node's every field but its instructions (and its output contract when asked), as JSON, so two nodes compare whole.</summary>
    private static string ShapeWithoutInstructions(DevWorkflowGraphNode node, bool withoutOutputSchema = false)
    {
        var shape = AssertEx.NotNull(JsonSerializer.SerializeToNode(node)).AsObject();
        _ = shape.Remove(nameof(DevWorkflowGraphNode.Instructions));
        if (withoutOutputSchema)
        {
            _ = shape.Remove(nameof(DevWorkflowGraphNode.OutputSchema));
        }

        return shape.ToJsonString();
    }

    /// <summary>
    ///     Both verification nodes hand in a verdict whose <c>refused</c> blocks the run and the refused task ids beside it; the
    ///     template parsing at all is <c>GRAPH-C4-5</c> accepting that contract on the unconditional edge into the integration gate.
    /// </summary>
    [Test]
    [Arguments(DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug)]
    [Arguments(DevWorkflowDefinitionSeeder.DevIntakeSlug)]
    public void TheShippedVerificationHandsInAVerdictWhoseRefusalBlocks(string slug)
    {
        var schema = AssertEx.NotNull(DevWorkflowGraph.Parse(TemplateFor(slug).Graph).Nodes["verify"].OutputSchema);

        var verdict = AssertEx.NotNull(schema.Find("verdict"));
        AssertEx.True(verdict.Required, "an answer left out would read as met.");
        AssertEx.Equal("approved, refused", string.Join(", ", AssertEx.NotNull(verdict.Enum)));
        AssertEx.Equal("refused", string.Join(", ", AssertEx.NotNull(verdict.UnmetOn)));
        AssertEx.Equal(expected: 1000, AssertEx.NotNull(schema.Find("refusedTasks")).MaxLength);
        AssertEx.Equal(expected: 2, schema.Properties.Count);
    }

    /// <summary>
    ///     The intake template ships valid, its classifier declares the triage contract, and every answer that contract
    ///     allows has its own edge — the save-time rule (<c>GRAPH-C4-5</c>) would refuse one left out, so this pins which
    ///     node each class reaches.
    /// </summary>
    [Test]
    public void TheShippedIntakeTemplateRoutesEveryClassOffItsClassifier()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.DevIntakeGraph);

        AssertEx.Equal(graph.Nodes.Count,
            DevWorkflowGraphContract.ValidateAndCountNodes(DevWorkflowDefinitionSeeder.DevIntakeGraph, new DevWorkflowOptions().MaxNodesPerDefinition));
        var schema = AssertEx.NotNull(graph.Nodes["classify"].OutputSchema);
        AssertEx.Equal("kind, area, severity, environment, evidenceQuality, rationale", string.Join(", ", schema.Properties.Select(static property => property.Name)));
        AssertEx.Equal("kind", string.Join(", ", schema.Properties.Where(static property => property.Required).Select(static property => property.Name)));
        AssertEx.Equal(expected: 1000, AssertEx.NotNull(schema.Find("rationale")).MaxLength);
        AssertEx.Equal("none, partial, good", string.Join(", ", AssertEx.NotNull(AssertEx.NotNull(schema.Find("evidenceQuality")).Enum)));

        var routes = graph.Edges.Where(static edge => edge.From == "classify")
                          .Select(static edge => $"{AssertEx.NotNull(edge.Condition).Value.GetString()}->{edge.To}")
                          .Order(StringComparer.Ordinal);
        AssertEx.Equal("bug->triage, feature->research, question->answer, unclear->needsinfo", string.Join(", ", routes));
        AssertEx.Equal(DevWorkflowJoinPolicy.Any, graph.Nodes["approved"].JoinPolicy, "either approval carries the run into the shared tail.");
        AssertEx.Equal("decompose, verify",
            string.Join(", ", graph.Edges.Where(static edge => edge.From == "approved").Select(static edge => edge.To).Order(StringComparer.Ordinal)));
        AssertEx.Equal(expected: 3,
            new[] { DevWorkflowDefinitionSeeder.ResearchPlanApprovalSlug, DevWorkflowDefinitionSeeder.FeatureDevelopmentSlug, DevWorkflowDefinitionSeeder.DevIntakeSlug }
                .Distinct(StringComparer.Ordinal).Count(),
            "a new template ships under a new slug, leaving the two before it as they are.");
    }

    /// <summary>
    ///     The tail is <c>feature-development-v1</c>'s copied: the same node types, retry targets, timeout, tool modes and
    ///     materialization, and the decomposition keeps every sentence of the contract it is held to.
    /// </summary>
    [Test]
    public void TheIntakeTailIsTheFeatureTemplatesTail()
    {
        var intake = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.DevIntakeGraph);
        var feature = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph);

        foreach (var key in new[] { "decompose", "implement", "validate", "join", "verify", "integrationapproval", "integrate", "fullvalidate" })
        {
            var copy = intake.Nodes[key];
            var original = feature.Nodes[key];
            AssertEx.Equal($"{original.NodeType}|{original.ToolMode}|{original.RetryTarget}|{original.NodeTimeoutSeconds}|{original.JoinPolicy}|{original.AgentSeedSlug}",
                $"{copy.NodeType}|{copy.ToolMode}|{copy.RetryTarget}|{copy.NodeTimeoutSeconds}|{copy.JoinPolicy}|{copy.AgentSeedSlug}",
                $"'{key}' is the feature template's node.");
        }

        var materialization = AssertEx.NotNull(intake.Nodes["decompose"].Materialization);
        var shipped = AssertEx.NotNull(feature.Nodes["decompose"].Materialization);
        AssertEx.Equal($"{shipped.TemplateNodeKey}|{shipped.ArtifactKind}|{shipped.JoinNodeKey}|{shipped.MaxChildren}",
            $"{materialization.TemplateNodeKey}|{materialization.ArtifactKind}|{materialization.JoinNodeKey}|{materialization.MaxChildren}");
        AssertEx.True(AssertEx.NotNull(intake.Nodes["decompose"].Instructions).EndsWith(AssertEx.NotNull(feature.Nodes["decompose"].Instructions), StringComparison.Ordinal),
            "the decomposition's contract is the feature template's, word for word, after the sentence about the path not taken.");
    }

    /// <summary>
    ///     The two tail nodes that sit behind both paths are told the other path's steps appear as skipped by design, or
    ///     they read the branch not taken as missing work; and every agent binds a persona the node already seeds.
    /// </summary>
    [Test]
    public void TheIntakeTemplateExplainsTheBranchNotTakenAndBindsOnlyShippedPersonas()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowDefinitionSeeder.DevIntakeGraph);

        foreach (var key in new[] { "decompose", "verify" })
        {
            var instructions = AssertEx.NotNull(graph.Nodes[key].Instructions);
            AssertEx.Contains(instructions, "\"Skipped steps\"");
            AssertEx.Contains(instructions, "not missing work");
        }

        AssertEx.Contains(AssertEx.NotNull(graph.Nodes["triage"].Instructions), "objectiveMet false", message: "thin evidence stands the run down rather than planning on a guess.");
        AssertEx.Equal(AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug, graph.Nodes["classify"].AgentSeedSlug);
        foreach (var node in graph.Nodes.Values.Where(static node => node.NodeType == DevWorkflowNodeType.Agent))
        {
            AssertEx.True(node.AgentSeedSlug is AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug or AgentDefaults.WorkSessionGeneralAgentSeedSlug,
                $"'{node.NodeKey}' binds '{node.AgentSeedSlug}', which is not a persona this template reuses.");
        }

        AssertEx.Contains(AssertEx.NotNull(graph.Nodes["classify"].Instructions), "name \"classification.md\"");
        AssertEx.Contains(AssertEx.NotNull(graph.Nodes["answer"].Instructions), "name \"answer.md\"");
        AssertEx.Contains(AssertEx.NotNull(graph.Nodes["needsinfo"].Instructions), "name \"questions.md\"", message: "an unclear request ends on the questions for its requester.");
    }
}
