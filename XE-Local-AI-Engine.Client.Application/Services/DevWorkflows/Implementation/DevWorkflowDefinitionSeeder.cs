namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Seeds the definition templates a node ships with, idempotently on their seed slug, so nobody starts from a blank
///     canvas.
///     <para>
///         Each template is parsed through the same validator a run start uses BEFORE it is written: a template that
///         would fail at run start fails at startup instead, where an operator can still be told about it.
///     </para>
///     <para>
///         Best-effort like every other seeder here: a node must start even when seeding fails, and the next startup
///         re-attempts. An ARCHIVED template is never resurrected — a changed template ships under a new slug, so
///         historical runs keep rendering from their own pinned snapshot.
///     </para>
///     <para>
///         <b>An UNTOUCHED seeded row follows the shipped seed; an operator's edit never does.</b> Insert-if-absent
///         alone left every existing installation — the operator's included — on whatever graph it was first seeded
///         with, so a template fix reached nobody who already had the template. So a row whose slug matches is compared
///         by graph hash, and one that differs is rewritten through the same update the definition PUT uses, which is
///         what gives it the same validation, the same concurrency check and the same version bump.
///     </para>
///     <para>
///         <b>Untouched means the row's graph is one THIS BUILD KNOWS IT SHIPPED</b> — its hash matches the current
///         template or one of the prior revisions kept beside it. Version is deliberately not the signal: the seeder's
///         own upgrade writes a version, so reading it would buy one catch-up per installation and then treat every
///         later change as an operator's edit. An archived row, or one whose graph matches no revision, is left exactly
///         as it is with the difference logged rather than silently applied. Runs are unaffected either way — a run
///         pins its own <c>GraphJson</c> at start and renders from that.
///     </para>
/// </summary>
public sealed class DevWorkflowDefinitionSeeder : IHostedService
{
    /// <summary>
    ///     The Slice A template: strictly linear, no repository needed, ending on the approval that is the point of it.
    ///     Both agent nodes bind by SEED SLUG rather than by id, because the personas they name are themselves seeded
    ///     and their ids differ per node.
    /// </summary>
    internal const string ResearchPlanApprovalSlug = "research-plan-approval";

    private const string ResearchPlanApprovalName = "Research → Plan → Approval";

    internal const string ResearchPlanApprovalGraph = $$"""
                                                       {
                                                         "schemaVersion": 1,
                                                         "nodes": [
                                                           {
                                                             "nodeKey": "research",
                                                             "nodeType": "Agent",
                                                             "label": "Research",
                                                             "agentSeedSlug": "{{AgentDefaults.WorkSessionResearchAgentSeedSlug}}",
                                                             "instructions": "Research what the request asks about and record what you find. Ground every claim in something you actually read. Before you complete this session you MUST call save_artifact exactly once, with name \"research.md\", mediaType \"text/markdown\", kind \"Report\", and the whole write-up as text. record_finding is for notes along the way and does NOT satisfy this step: the next node reads your artifact, not your findings."
                                                           },
                                                           {
                                                             "nodeKey": "plan",
                                                             "nodeType": "Agent",
                                                             "label": "Plan",
                                                             "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                             "instructions": "Turn the research into a plan a person can approve: what to do, in what order, and what would show it worked. Before you complete this session you MUST call save_artifact exactly once, with name \"plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
                                                           },
                                                           {
                                                             "nodeKey": "approve",
                                                             "nodeType": "HumanGate",
                                                             "label": "Approve the plan"
                                                           }
                                                         ],
                                                         "edges": [
                                                           { "from": "research", "to": "plan" },
                                                           { "from": "plan", "to": "approve" }
                                                         ]
                                                       }
                                                       """;

    /// <summary>
    ///     The Slice C template (§5.10): research and a plan a human approves, a decomposition that expands into one
    ///     implementation-and-validation subtree per task, and an integration stage that applies nothing until a second
    ///     human gate says so.
    ///     <para>
    ///         Both gates route on <c>decision eq "Approve"</c> and have no other branch, which is what makes a refusal
    ///         end the run rather than continue past it — for the integration gate that is Y3 itself, and the parser
    ///         enforces the same rule structurally.
    ///     </para>
    ///     <para>
    ///         The implementation node declares a timeout because nothing else bounds it: Dev Mode bounds each attempt
    ///         and each review round, but not attempts back to back. Two hours is a slice of a feature, generously.
    ///     </para>
    ///     <para>
    ///         <b>Two of these edges are load-bearing for EVIDENCE rather than for routing.</b> Upstream artifacts
    ///         resolve back through structural nodes to the nearest PRODUCING ancestors, and a producing TYPE stops that
    ///         walk on its own path whether or not it produced anything — so the join's inbound edge from the
    ///         unmaterialized <c>validate</c> template node contributes nothing, and <c>decompose → join</c> is what
    ///         puts the task package on a path back from the join at all. The materializer keeps it for that reason.
    ///     </para>
    ///     <para>
    ///         <b>The second fix loop is what makes staleness reachable at all.</b> <c>validate</c>'s own
    ///         <c>retryTarget</c> fires before anything downstream of it exists, so nothing has yet recorded consuming
    ///         the work it replaces and the mark finds no dependents — every run of this template reported zero.
    ///         <c>fullvalidate</c>'s routes a failure of the INTEGRATED result back to <c>verify</c>, by which point
    ///         <c>integrationapproval</c> has been handed <c>verification.md</c> as its evidence and <c>integrate</c>
    ///         has consumed it too: the re-run supersedes it, and the apply report and the full check's own report are
    ///         flagged as written from a version that no longer exists.
    ///     </para>
    ///     <para>
    ///         <b>It targets <c>verify</c> and not <c>implement</c>, and that is not a preference.</b> <c>implement</c>
    ///         is the materialization TEMPLATE node: run seeding skips template keys and the materializer rewrites a
    ///         retryTarget only for clones inside the subtree, so a node outside it naming <c>implement</c> names a node
    ///         run that never exists — the route would find no row and block the run on <c>Configuration</c> instead of
    ///         re-attempting anything. The parser now refuses that at authoring time. Targeting <c>verify</c> is also
    ///         the safe direction: the implementations are not reset, their tasks stay Completed, and the apply node's
    ///         re-run short-circuits per task rather than applying a patch twice.
    ///     </para>
    ///     <para>
    ///         <b>v1 ceiling:</b> what a re-run does NOT flag is <c>validate</c>'s own report. Only an agent node's
    ///         promotion and a tool node's report write a workflow artifact, and <c>implement</c> is a DevTask — the
    ///         patch it produces is a Dev Mode artifact in another store, which the artifact-use table cannot name. So
    ///         <c>validate</c> records consuming nothing and there is nothing for a re-run to supersede. Closing it
    ///         means promoting the approved patch into the run's own artifacts, which is a producer this lane does not
    ///         have yet.
    ///     </para>
    ///     <para>
    ///         That edge alone does NOT carry the approved plan, and the first live run said so in its own words: the
    ///         walk stops at <c>decompose</c>, which produced the task package, so <c>plan.md</c> is one producer
    ///         further back and out of reach. <c>planapproval → verify</c> is the edge that carries it. It is
    ///         conditioned on the approval like the decomposition's, so a declined plan kills both paths into the
    ///         verification rather than leaving it half-fed, and it costs no routing: the gate has long since settled by
    ///         the time the join lets anything through.
    ///     </para>
    /// </summary>
    internal const string FeatureDevelopmentSlug = "feature-development-v1";

    private const string FeatureDevelopmentName = "Feature Development v1";

    internal const string FeatureDevelopmentGraph = $$"""
                                                    {
                                                      "schemaVersion": 1,
                                                      "nodes": [
                                                        {
                                                          "nodeKey": "research",
                                                          "nodeType": "Agent",
                                                          "label": "Research",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionResearchAgentSeedSlug}}",
                                                          "instructions": "Research what this feature touches in this repository: the code that would change, the conventions it follows, and what already exists that you should reuse. Ground every claim in something you actually read. Before you complete this session you MUST call save_artifact exactly once, with name \"research.md\", mediaType \"text/markdown\", kind \"Report\", and the whole write-up as text."
                                                        },
                                                        {
                                                          "nodeKey": "plan",
                                                          "nodeType": "Agent",
                                                          "label": "Plan",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Turn the research into a plan a person can approve: what to build, in what order, and what would show it worked. Before you complete this session you MUST call save_artifact exactly once, with name \"plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
                                                        },
                                                        {
                                                          "nodeKey": "planapproval",
                                                          "nodeType": "HumanGate",
                                                          "label": "Approve the plan"
                                                        },
                                                        {
                                                          "nodeKey": "decompose",
                                                          "nodeType": "Agent",
                                                          "label": "Decompose",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Split the approved plan into implementation tasks. Default to ONE task: a request that names one method, one file or one behaviour is a single task that implements it and adds its test file together, and splitting it only buys failures. Split only where the slices are independent features a build can judge separately; ten tasks is the ceiling. Every task must change code — never emit a survey, a read-only investigation or a verify-only slice, and never chain a \"build and verify\" task, because validation runs automatically after every task. Tests a task adds go in a NEW test file named in \"changes\"; a test file that already exists may not be edited. Before you complete this session you MUST call save_artifact exactly once, with name \"tasks.json\", mediaType \"application/json\", kind \"Report\", and a JSON array as the text: [{\"id\": \"short-slug\", \"title\": \"what it is\", \"goal\": \"what to implement, in full — this becomes the task's requirements and it is all the coder is told\", \"changes\": [\"src/…\", \"tests/…NewFile.cs\"], \"dependsOn\": [\"another-id\"], \"acceptanceCriteria\": [\"how to tell it is done\"]}]. Ids must be unique, dependsOn may only name ids in this same array, and there must be no dependency cycle. \"changes\" must name at least one workspace-relative file the task will add or edit. If the plan needs no implementation work, answer with an empty array rather than inventing one.",
                                                          "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 10 }
                                                        },
                                                        {
                                                          "nodeKey": "implement",
                                                          "nodeType": "DevTask",
                                                          "label": "Implement",
                                                          "nodeTimeoutSeconds": 7200
                                                        },
                                                        {
                                                          "nodeKey": "validate",
                                                          "nodeType": "Tool",
                                                          "label": "Validate",
                                                          "retryTarget": "implement"
                                                        },
                                                        {
                                                          "nodeKey": "join",
                                                          "nodeType": "Join",
                                                          "label": "Every slice implemented"
                                                        },
                                                        {
                                                          "nodeKey": "verify",
                                                          "nodeType": "Agent",
                                                          "label": "Verify",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Judge the implemented slices against the approved plan, independently: read what each task produced and say whether the feature is actually there, what is missing, and what you would not sign off. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text."
                                                        },
                                                        {
                                                          "nodeKey": "integrationapproval",
                                                          "nodeType": "HumanGate",
                                                          "label": "Approve integration"
                                                        },
                                                        {
                                                          "nodeKey": "integrate",
                                                          "nodeType": "Tool",
                                                          "toolMode": "Apply",
                                                          "label": "Apply the approved patches"
                                                        },
                                                        {
                                                          "nodeKey": "fullvalidate",
                                                          "nodeType": "Tool",
                                                          "label": "Validate the integrated result",
                                                          "retryTarget": "verify"
                                                        }
                                                      ],
                                                      "edges": [
                                                        { "from": "research", "to": "plan" },
                                                        { "from": "plan", "to": "planapproval" },
                                                        { "from": "planapproval", "to": "decompose", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "decompose", "to": "join" },
                                                        { "from": "implement", "to": "validate" },
                                                        { "from": "validate", "to": "join" },
                                                        { "from": "join", "to": "verify" },
                                                        { "from": "planapproval", "to": "verify", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "verify", "to": "integrationapproval" },
                                                        { "from": "integrationapproval", "to": "integrate", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "integrate", "to": "fullvalidate" }
                                                      ]
                                                    }
                                                    """;

    /// <summary>
    ///     The <c>feature-development-v1</c> graph the first build shipped, kept so an installation still holding it
    ///     verbatim can be brought up to the current one. Every constant below it is another such graph, and every one
    ///     of them is passed to <c>SeedAsync</c> as a prior revision: editing the live template means copying it here
    ///     FIRST, byte for byte, or the catch-up stops recognising the installations that are on it and silently leaves
    ///     them behind. A revision leaves this list only when no installation could still be on it, which is not a thing
    ///     this code can know — so they stay.
    /// </summary>
    internal const string FeatureDevelopmentGraphRevision1 = $$"""
                                                    {
                                                      "schemaVersion": 1,
                                                      "nodes": [
                                                        {
                                                          "nodeKey": "research",
                                                          "nodeType": "Agent",
                                                          "label": "Research",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionResearchAgentSeedSlug}}",
                                                          "instructions": "Research what this feature touches in this repository: the code that would change, the conventions it follows, and what already exists that you should reuse. Ground every claim in something you actually read. Before you complete this session you MUST call save_artifact exactly once, with name \"research.md\", mediaType \"text/markdown\", kind \"Report\", and the whole write-up as text."
                                                        },
                                                        {
                                                          "nodeKey": "plan",
                                                          "nodeType": "Agent",
                                                          "label": "Plan",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Turn the research into a plan a person can approve: what to build, in what order, and what would show it worked. Before you complete this session you MUST call save_artifact exactly once, with name \"plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
                                                        },
                                                        {
                                                          "nodeKey": "planapproval",
                                                          "nodeType": "HumanGate",
                                                          "label": "Approve the plan"
                                                        },
                                                        {
                                                          "nodeKey": "decompose",
                                                          "nodeType": "Agent",
                                                          "label": "Decompose",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Split the approved plan into independent implementation tasks, each one a slice a coder can finish and a build can judge on its own. Before you complete this session you MUST call save_artifact exactly once, with name \"tasks.json\", mediaType \"application/json\", kind \"Report\", and a JSON array as the text: [{\"id\": \"short-slug\", \"title\": \"what it is\", \"goal\": \"what to implement, in full — this becomes the task's requirements and it is all the coder is told\", \"dependsOn\": [\"another-id\"], \"acceptanceCriteria\": [\"how to tell it is done\"]}]. Ids must be unique, dependsOn may only name ids in this same array, and there must be no dependency cycle. Ten tasks is the ceiling. If the plan needs no implementation work, answer with an empty array rather than inventing one.",
                                                          "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 10 }
                                                        },
                                                        {
                                                          "nodeKey": "implement",
                                                          "nodeType": "DevTask",
                                                          "label": "Implement",
                                                          "nodeTimeoutSeconds": 7200
                                                        },
                                                        {
                                                          "nodeKey": "validate",
                                                          "nodeType": "Tool",
                                                          "label": "Validate",
                                                          "retryTarget": "implement"
                                                        },
                                                        {
                                                          "nodeKey": "join",
                                                          "nodeType": "Join",
                                                          "label": "Every slice implemented"
                                                        },
                                                        {
                                                          "nodeKey": "verify",
                                                          "nodeType": "Agent",
                                                          "label": "Verify",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Judge the implemented slices against the approved plan, independently: read what each task produced and say whether the feature is actually there, what is missing, and what you would not sign off. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text."
                                                        },
                                                        {
                                                          "nodeKey": "integrationapproval",
                                                          "nodeType": "HumanGate",
                                                          "label": "Approve integration"
                                                        },
                                                        {
                                                          "nodeKey": "integrate",
                                                          "nodeType": "Tool",
                                                          "toolMode": "Apply",
                                                          "label": "Apply the approved patches"
                                                        },
                                                        {
                                                          "nodeKey": "fullvalidate",
                                                          "nodeType": "Tool",
                                                          "label": "Validate the integrated result"
                                                        }
                                                      ],
                                                      "edges": [
                                                        { "from": "research", "to": "plan" },
                                                        { "from": "plan", "to": "planapproval" },
                                                        { "from": "planapproval", "to": "decompose", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "decompose", "to": "join" },
                                                        { "from": "implement", "to": "validate" },
                                                        { "from": "validate", "to": "join" },
                                                        { "from": "join", "to": "verify" },
                                                        { "from": "planapproval", "to": "verify", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "verify", "to": "integrationapproval" },
                                                        { "from": "integrationapproval", "to": "integrate", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "integrate", "to": "fullvalidate" }
                                                      ]
                                                    }
                                                    """;

    /// <summary>
    ///     The revision this build replaces: <see cref="FeatureDevelopmentGraphRevision1" /> with the
    ///     <c>fullvalidate</c> retry target added, and with the decomposition instructions that never told the model a
    ///     slice has to change code — which is what four live runs spent themselves on. Kept for the same reason
    ///     revision 1 is.
    /// </summary>
    internal const string FeatureDevelopmentGraphRevision2 = $$"""
                                                    {
                                                      "schemaVersion": 1,
                                                      "nodes": [
                                                        {
                                                          "nodeKey": "research",
                                                          "nodeType": "Agent",
                                                          "label": "Research",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionResearchAgentSeedSlug}}",
                                                          "instructions": "Research what this feature touches in this repository: the code that would change, the conventions it follows, and what already exists that you should reuse. Ground every claim in something you actually read. Before you complete this session you MUST call save_artifact exactly once, with name \"research.md\", mediaType \"text/markdown\", kind \"Report\", and the whole write-up as text."
                                                        },
                                                        {
                                                          "nodeKey": "plan",
                                                          "nodeType": "Agent",
                                                          "label": "Plan",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Turn the research into a plan a person can approve: what to build, in what order, and what would show it worked. Before you complete this session you MUST call save_artifact exactly once, with name \"plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
                                                        },
                                                        {
                                                          "nodeKey": "planapproval",
                                                          "nodeType": "HumanGate",
                                                          "label": "Approve the plan"
                                                        },
                                                        {
                                                          "nodeKey": "decompose",
                                                          "nodeType": "Agent",
                                                          "label": "Decompose",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Split the approved plan into independent implementation tasks, each one a slice a coder can finish and a build can judge on its own. Before you complete this session you MUST call save_artifact exactly once, with name \"tasks.json\", mediaType \"application/json\", kind \"Report\", and a JSON array as the text: [{\"id\": \"short-slug\", \"title\": \"what it is\", \"goal\": \"what to implement, in full — this becomes the task's requirements and it is all the coder is told\", \"dependsOn\": [\"another-id\"], \"acceptanceCriteria\": [\"how to tell it is done\"]}]. Ids must be unique, dependsOn may only name ids in this same array, and there must be no dependency cycle. Ten tasks is the ceiling. If the plan needs no implementation work, answer with an empty array rather than inventing one.",
                                                          "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 10 }
                                                        },
                                                        {
                                                          "nodeKey": "implement",
                                                          "nodeType": "DevTask",
                                                          "label": "Implement",
                                                          "nodeTimeoutSeconds": 7200
                                                        },
                                                        {
                                                          "nodeKey": "validate",
                                                          "nodeType": "Tool",
                                                          "label": "Validate",
                                                          "retryTarget": "implement"
                                                        },
                                                        {
                                                          "nodeKey": "join",
                                                          "nodeType": "Join",
                                                          "label": "Every slice implemented"
                                                        },
                                                        {
                                                          "nodeKey": "verify",
                                                          "nodeType": "Agent",
                                                          "label": "Verify",
                                                          "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                          "instructions": "Judge the implemented slices against the approved plan, independently: read what each task produced and say whether the feature is actually there, what is missing, and what you would not sign off. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text."
                                                        },
                                                        {
                                                          "nodeKey": "integrationapproval",
                                                          "nodeType": "HumanGate",
                                                          "label": "Approve integration"
                                                        },
                                                        {
                                                          "nodeKey": "integrate",
                                                          "nodeType": "Tool",
                                                          "toolMode": "Apply",
                                                          "label": "Apply the approved patches"
                                                        },
                                                        {
                                                          "nodeKey": "fullvalidate",
                                                          "nodeType": "Tool",
                                                          "label": "Validate the integrated result",
                                                          "retryTarget": "verify"
                                                        }
                                                      ],
                                                      "edges": [
                                                        { "from": "research", "to": "plan" },
                                                        { "from": "plan", "to": "planapproval" },
                                                        { "from": "planapproval", "to": "decompose", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "decompose", "to": "join" },
                                                        { "from": "implement", "to": "validate" },
                                                        { "from": "validate", "to": "join" },
                                                        { "from": "join", "to": "verify" },
                                                        { "from": "planapproval", "to": "verify", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "verify", "to": "integrationapproval" },
                                                        { "from": "integrationapproval", "to": "integrate", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                        { "from": "integrate", "to": "fullvalidate" }
                                                      ]
                                                    }
                                                    """;

    /// <summary>
    ///     Every <c>feature-development-v1</c> graph this build knows it published, which is the list an untouched row is
    ///     recognised by. Hoisted out of the call site so a test can assert a kept revision is actually IN it: a copy that
    ///     never reaches <see cref="SeedAsync" /> looks right in the file and silently strands every installation on it.
    /// </summary>
    internal static readonly string[] FeatureDevelopmentPriorRevisions = [FeatureDevelopmentGraphRevision1, FeatureDevelopmentGraphRevision2];

    private readonly ILogger<DevWorkflowDefinitionSeeder> _logger;
    private readonly DevWorkflowOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;

    public DevWorkflowDefinitionSeeder(IServiceScopeFactory scopeFactory,
        IOptions<DevWorkflowOptions> options,
        ILogger<DevWorkflowDefinitionSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
            await SeedAsync(store, ResearchPlanApprovalSlug, ResearchPlanApprovalName, ResearchPlanApprovalGraph, [], cancellationToken).ConfigureAwait(false);
            await SeedAsync(store, FeatureDevelopmentSlug, FeatureDevelopmentName, FeatureDevelopmentGraph, FeatureDevelopmentPriorRevisions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to seed.
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or DbUpdateException)
        {
            _logger.LogWarning(exception, "Development workflow template seeding failed at startup; the templates may be missing until the next start.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task SeedAsync(IDevWorkflowStore store,
        string seedSlug,
        string name,
        string graphJson,
        IReadOnlyList<string> priorRevisions,
        CancellationToken cancellationToken)
    {
        var existing = (await store.ListDefinitionsAsync(includeArchived: true, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(definition => string.Equals(definition.SeedSlug, seedSlug, StringComparison.Ordinal));

        // The store hashes a definition's graph bytes at every save, so this answers "is this row the graph this build
        // ships" without decrypting a blob to read it — and it is the graph BYTES on both sides, which is the right
        // comparison here precisely because an untouched seeded row holds a shipped constant verbatim.
        if (existing is not null && string.Equals(existing.GraphHash, GraphHash(graphJson), StringComparison.Ordinal))
        {
            return;
        }

        // Untouched is "this row is a graph WE shipped", not "this row has never been written". Reading it off the
        // version instead would let the seeder's own upgrade take the row to 2 and make every later template change
        // look like an operator's edit — one catch-up per installation and then silence. Matching the content against
        // the revisions this build knows it published carries across as many of them as there are, and still cannot
        // mistake an operator's graph for one of ours: they would have had to type it byte for byte.
        if (existing is { } row && (row.Archived || !priorRevisions.Any(revision => string.Equals(row.GraphHash, GraphHash(revision), StringComparison.Ordinal))))
        {
            _logger.LogInformation("The {Name} workflow definition {DefinitionId} (slug {SeedSlug}) differs from the template this build ships and from every "
                                   + "revision it knows it published, so it was left as it is: it has been edited or deleted since it was seeded.",
                name,
                row.Id,
                seedSlug);
            return;
        }

        var graph = DevWorkflowGraph.Parse(graphJson);
        if (existing is null)
        {
            var seeded = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(),
                                            name,
                                            graphJson,
                                            graph.Nodes.Count,
                                            DevWorkflowDefinitionSource.Seeded,
                                            seedSlug),
                                        cancellationToken)
                                    .ConfigureAwait(false);
            _logger.LogInformation("Seeded the {Name} workflow definition {DefinitionId} (slug {SeedSlug}).", name, seeded.Id, seedSlug);
            return;
        }

        // The GRAPH only. A name is the operator's to choose — renaming a seeded template is a name-only PUT that leaves
        // the graph one of ours, so it still qualifies for the catch-up, and passing the shipped name here would revert
        // their label as a side effect of a fix they never asked about.
        var upgraded = await store.UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand(existing.Id, existing.Version, Name: null, graphJson, graph.Nodes.Count),
                                      cancellationToken)
                                  .ConfigureAwait(false);
        _logger.LogInformation("Updated the untouched {Name} workflow definition {DefinitionId} (slug {SeedSlug}) to the template this build ships, version {Version}.",
            name,
            upgraded.Id,
            seedSlug,
            upgraded.Version);
    }

    /// <summary>The hash the store writes beside a definition's graph, computed the same way over the same bytes.</summary>
    internal static string GraphHash(string graphJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(graphJson)));
}
