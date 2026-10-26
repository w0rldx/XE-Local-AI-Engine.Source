namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Seeds the definition templates a node ships with, idempotently on their seed slug, so nobody starts from a
///     blank canvas.
/// </summary>
/// <remarks>
///     An UNTOUCHED seeded row follows the shipped seed; an operator's edit never does. Insert-if-absent alone leaves
///     every existing installation on whatever graph it was first seeded with, so a row whose slug matches is
///     compared by GRAPH HASH against what this build knows it shipped, and one that differs is rewritten through
///     the same update the definition PUT uses. Best-effort: a node must start even when seeding fails. See
///     docs/wiki/25-dev-workflows.md ("Seeded templates").
/// </remarks>
public sealed class DevWorkflowDefinitionSeeder : IHostedService
{
    /// <summary>
    ///     The <c>research-plan-approval</c> template: strictly linear, no repository needed, ending on the approval
    ///     that is the point of it.
    /// </summary>
    /// <remarks>
    ///     Both agent nodes bind by SEED SLUG rather than by id, because the personas they name are themselves seeded
    ///     and their ids differ per node.
    /// </remarks>
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
    ///     The <c>feature-development-v1</c> template: research and an approved plan, a decomposition that expands
    ///     into one implementation-and-validation subtree per task, and an integration stage behind a second gate.
    /// </summary>
    /// <remarks>
    ///     Both gates route on the approve decision and carry no other branch, so a refusal ends the run — which for
    ///     the integration gate IS the rule that an apply node is reached from a human gate and nothing else.
    ///     <c>decompose → join</c> and <c>planapproval → verify</c> are load-bearing for EVIDENCE, not routing, and
    ///     <c>fullvalidate</c> targets <c>verify</c> because <c>implement</c> is a template key no node run ever has.
    ///     See docs/wiki/25-dev-workflows.md ("Seeded templates").
    /// </remarks>
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
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
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
                                                            "instructions": "Judge the implemented slices against the approved plan (plan.md), independently, from the patches themselves. Each task's validation step publishes the patch it tested as an artifact named after that step with .patch on the end (validate#<task id>.patch), beside that task's validation report: judge each patch against the plan and that report, and say whether the feature is actually there, what is missing, and what you would not sign off. Read every patch to its end: call read_upstream_artifact with its name, and again with the nextOffset it hands back, until it answers the end of the artifact, even when the objective already shows the whole patch. The engine records the pages it served you, and a patch you did not read to its end stops the run whatever your report says. When a task's validation report says its patch was not published, you cannot read that change: say so and why, and do not sign it off blind. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text. Then complete it with output verdict \"approved\" only when you sign off every task, and otherwise verdict \"refused\" with refusedTasks listing the ids of every task you would not sign off: a refused verdict stops the run for a person, whatever objectiveMet says.",
                                                            "outputSchema": { "type": "object", "required": ["verdict"], "additionalProperties": false, "properties": { "verdict": { "type": "string", "enum": ["approved", "refused"], "unmetOn": ["refused"] }, "refusedTasks": { "type": "string", "maxLength": 1000 } } }
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
    ///     verbatim can be brought up to the current one.
    /// </summary>
    /// <remarks>
    ///     Every constant below it is another such graph, and each is passed to <c>SeedAsync</c> as a prior revision:
    ///     editing the live template means copying it here FIRST, byte for byte, or the catch-up stops recognising
    ///     the installations that are on it and silently leaves them behind. A revision leaves this list only when no
    ///     installation could still be on it, which is not a thing this code can know — so they stay.
    /// </remarks>
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
    ///     <c>fullvalidate</c> retry target added.
    /// </summary>
    /// <remarks>
    ///     Its decomposition instructions never told the model a slice has to change code, which is what made runs
    ///     spend themselves on slices no coder could complete. Kept for the same reason revision 1 is.
    /// </remarks>
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
    ///     The revision this build replaces: <see cref="FeatureDevelopmentGraphRevision2" /> with the decomposition told what a slice has to be.
    /// </summary>
    /// <remarks>
    ///     Its research node ran the knowledge-base persona, which has no way to read the repository the work item names, so every run
    ///     blocked on its first node. Kept for the same reason revision 1 is.
    /// </remarks>
    internal const string FeatureDevelopmentGraphRevision3 = $$"""
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
    ///     The revision this build replaces: <see cref="FeatureDevelopmentGraphRevision3" /> with the research node bound to the
    ///     repository-research persona.
    /// </summary>
    /// <remarks>
    ///     Its verification node was told to judge "what each task produced" when no patch reached it, so it blocked on every run
    ///     for want of the diff. Kept for the same reason revision 1 is.
    /// </remarks>
    internal const string FeatureDevelopmentGraphRevision4 = $$"""
                                                               {
                                                                 "schemaVersion": 1,
                                                                 "nodes": [
                                                                   {
                                                                     "nodeKey": "research",
                                                                     "nodeType": "Agent",
                                                                     "label": "Research",
                                                                     "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
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
    ///     The revision this build replaces: <see cref="FeatureDevelopmentGraphRevision4" /> with verify judging the published patches,
    ///     paged through <c>read_upstream_artifact</c>.
    /// </summary>
    /// <remarks>
    ///     Its verify could close met while refusing a task in its report, and say a patch was read in full when it was not.
    ///     Kept for the same reason revision 1 is.
    /// </remarks>
    internal const string FeatureDevelopmentGraphRevision5 = $$"""
                                                               {
                                                                 "schemaVersion": 1,
                                                                 "nodes": [
                                                                   {
                                                                     "nodeKey": "research",
                                                                     "nodeType": "Agent",
                                                                     "label": "Research",
                                                                     "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
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
                                                                     "instructions": "Judge the implemented slices against the approved plan (plan.md), independently, from the patches themselves. Each task's validation step publishes the patch it tested as an artifact named after that step with .patch on the end (validate#<task id>.patch), beside that task's validation report: judge each patch against the plan and that report, and say whether the feature is actually there, what is missing, and what you would not sign off. A patch shown only in part, or by its header alone, says so: call read_upstream_artifact with its name, and again with the nextOffset it hands back, until you have read the whole patch, and sign off only on what you actually read. When a task's validation report says its patch was not published, you cannot read that change: say so and why, do not sign it off blind, and complete the session with objectiveMet false naming that task. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text."
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
    ///     The <c>dev-intake-v1</c> template: a classifier whose <c>output.kind</c> routes a feature or a bug to its own
    ///     plan gate and then one shared tail, a question to an answer, and an unclear request to what is missing.
    /// </summary>
    /// <remarks>
    ///     Both gates meet in <c>approved</c>, an <c>Any</c> join that alone feeds the tail. Per-gate evidence edges into
    ///     <c>verify</c> would skip it (the gate not taken leaves a Dead edge under <c>All</c>), and <c>verify</c> on
    ///     <c>Any</c> is refused by <c>GRAPH-C4-3</c>. The tail is <c>feature-development-v1</c>'s, copied. See
    ///     docs/wiki/25-dev-workflows.md ("Seeded templates").
    /// </remarks>
    internal const string DevIntakeSlug = "dev-intake-v1";

    private const string DevIntakeName = "Development intake (classify, then route)";

    internal const string DevIntakeGraph = $$"""
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              {
                                                "nodeKey": "classify",
                                                "nodeType": "Agent",
                                                "label": "Classify the request",
                                                "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                "instructions": "Classify the operator's request so the run takes the right path. Read enough of this repository to tell where the work would land, and do not plan, fix or answer anything here. kind: \"feature\" for new behaviour or a change to existing behaviour (a change request IS a feature), \"bug\" for behaviour that is wrong today, \"question\" for a request that wants an explanation and no change, and \"unclear\" when the request cannot be acted on as written: it names no behaviour, contradicts itself, or leaves out what anyone would have to ask first. area: where the work lands. severity and environment describe a bug and are \"unknown\" for anything else. evidenceQuality: \"none\", \"partial\" (a symptom without steps, logs or a failing case) or \"good\" (steps, logs or a failing case). rationale: one or two sentences on why this kind. Before you complete this session you MUST call save_artifact exactly once, with name \"classification.md\", mediaType \"text/markdown\", kind \"Report\", and every field you hand in as output together with your rationale as text: the nodes after you read that artifact, not your output.",
                                                "outputSchema": { "type": "object", "required": ["kind"], "additionalProperties": false, "properties": { "kind": { "type": "string", "enum": ["feature", "bug", "question", "unclear"] }, "area": { "type": "string", "enum": ["frontend", "backend", "fullstack", "infra", "unknown"] }, "severity": { "type": "string", "enum": ["cosmetic", "minor", "major", "critical", "unknown"] }, "environment": { "type": "string", "enum": ["production", "development", "unknown"] }, "evidenceQuality": { "type": "string", "enum": ["none", "partial", "good"] }, "rationale": { "type": "string", "maxLength": 1000 } } }
                                              },
                                              {
                                                "nodeKey": "answer",
                                                "nodeType": "Agent",
                                                "label": "Answer the question",
                                                "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                "instructions": "Answer the operator's question from this repository. Read the code, configuration and documentation the question is about and ground every statement in something you actually read, naming the files and symbols. Change nothing: this path ends with your answer. If the repository does not hold the answer, say so plainly and say what you looked at. Before you complete this session you MUST call save_artifact exactly once, with name \"answer.md\", mediaType \"text/markdown\", kind \"Report\", and the whole answer as text."
                                              },
                                              {
                                                "nodeKey": "needsinfo",
                                                "nodeType": "Agent",
                                                "label": "Say what is missing",
                                                "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                "instructions": "The request was classified as unclear, so this run ends with a note to the operator rather than a guess. Say what is missing before anyone can act on it: the behaviour wanted or the behaviour observed, where it happens, how to reproduce it, the result expected, and anything the classification names. Make each point a question the operator can answer in a line or two, and say what kind of request (feature, bug or question) it would become once answered, if you can tell. Do not research, plan or change anything. Before you complete this session you MUST call save_artifact exactly once, with name \"what-is-missing.md\", mediaType \"text/markdown\", kind \"Report\", and the whole note as text. The operator adds what it names to the work item and starts a new run."
                                              },
                                              {
                                                "nodeKey": "research",
                                                "nodeType": "Agent",
                                                "label": "Research",
                                                "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
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
                                                "nodeKey": "triage",
                                                "nodeType": "Agent",
                                                "label": "Triage",
                                                "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                "instructions": "Triage the reported bug, starting from the classification before you: its area says where to look, its environment whether this is happening in production or in development, and its evidence quality how far the report can be trusted. Restate the symptom, the behaviour expected instead and how to reproduce it, then locate the code the area points at and confirm the reported path exists there. When the evidence is too thin to reproduce or locate the fault (evidence quality \"none\", or a symptom nothing in the repository explains), do not guess: write down exactly what is missing, save the artifact, and complete the session with objectiveMet false, so a person is asked instead of a fix being planned on a guess. Before you complete this session you MUST call save_artifact exactly once, with name \"triage.md\", mediaType \"text/markdown\", kind \"Report\", and the whole triage as text."
                                              },
                                              {
                                                "nodeKey": "rootcause",
                                                "nodeType": "Agent",
                                                "label": "Find the root cause",
                                                "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                "instructions": "Find the root cause of the triaged bug in this repository. Start from the code the triage located and trace the faulty behaviour back to where it originates, not where it shows, and check every caller of the code you suspect: a cause in a shared path breaks all of them. Ground every claim in something you actually read, naming the files and symbols. If you cannot reach a cause you would stake a fix on, say what you ruled out and what you would need, and complete the session with objectiveMet false. Before you complete this session you MUST call save_artifact exactly once, with name \"rootcause.md\", mediaType \"text/markdown\", kind \"Report\", and the whole analysis as text."
                                              },
                                              {
                                                "nodeKey": "fixplan",
                                                "nodeType": "Agent",
                                                "label": "Plan the fix",
                                                "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                "instructions": "Turn the root cause into a fix plan a person can approve: the change that removes the cause rather than hiding the symptom, where it goes, the test that fails before the fix and passes after it, and what else the change could affect. Before you complete this session you MUST call save_artifact exactly once, with name \"fix-plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
                                              },
                                              {
                                                "nodeKey": "fixapproval",
                                                "nodeType": "HumanGate",
                                                "label": "Approve the fix plan"
                                              },
                                              {
                                                "nodeKey": "approved",
                                                "nodeType": "Join",
                                                "joinPolicy": "Any",
                                                "label": "An approved plan"
                                              },
                                              {
                                                "nodeKey": "decompose",
                                                "nodeType": "Agent",
                                                "label": "Decompose",
                                                "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                "instructions": "This run took one of two paths, a feature plan or a bug-fix plan, and the steps of the path it did not take are listed under \"Skipped steps\": that is the branch not taken, expected and not missing work, so do not report it. Split the approved plan into implementation tasks. Default to ONE task: a request that names one method, one file or one behaviour is a single task that implements it and adds its test file together, and splitting it only buys failures. Split only where the slices are independent features a build can judge separately; ten tasks is the ceiling. Every task must change code — never emit a survey, a read-only investigation or a verify-only slice, and never chain a \"build and verify\" task, because validation runs automatically after every task. Tests a task adds go in a NEW test file named in \"changes\"; a test file that already exists may not be edited. Before you complete this session you MUST call save_artifact exactly once, with name \"tasks.json\", mediaType \"application/json\", kind \"Report\", and a JSON array as the text: [{\"id\": \"short-slug\", \"title\": \"what it is\", \"goal\": \"what to implement, in full — this becomes the task's requirements and it is all the coder is told\", \"changes\": [\"src/…\", \"tests/…NewFile.cs\"], \"dependsOn\": [\"another-id\"], \"acceptanceCriteria\": [\"how to tell it is done\"]}]. Ids must be unique, dependsOn may only name ids in this same array, and there must be no dependency cycle. \"changes\" must name at least one workspace-relative file the task will add or edit. If the plan needs no implementation work, answer with an empty array rather than inventing one.",
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
                                                "instructions": "Judge the implemented slices against the approved plan (plan.md for a feature, fix-plan.md for a bug fix), independently, from the patches themselves. Each task's validation step publishes the patch it tested as an artifact named after that step with .patch on the end (validate#<task id>.patch), beside that task's validation report: judge each patch against the plan and that report, and say whether the planned change is actually there, what is missing, and what you would not sign off. For a bug-fix plan, say whether the change removes the root cause the plan names and whether a test now shows it. Read every patch to its end: call read_upstream_artifact with its name, and again with the nextOffset it hands back, until it answers the end of the artifact, even when the objective already shows the whole patch. The engine records the pages it served you, and a patch you did not read to its end stops the run whatever your report says. When a task's validation report says its patch was not published, you cannot read that change: say so and why, and do not sign it off blind. This run took one of two paths, a feature plan or a bug-fix plan, and the steps of the path it did not take are listed under \"Skipped steps\": that is the branch not taken, expected and not missing work, so do not report it. A skipped implementation slice is different: that work did not arrive, so judge without it and say what it leaves undone. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text. Then complete it with output verdict \"approved\" only when you sign off every task, and otherwise verdict \"refused\" with refusedTasks listing the ids of every task you would not sign off: a refused verdict stops the run for a person, whatever objectiveMet says.",
                                                "outputSchema": { "type": "object", "required": ["verdict"], "additionalProperties": false, "properties": { "verdict": { "type": "string", "enum": ["approved", "refused"], "unmetOn": ["refused"] }, "refusedTasks": { "type": "string", "maxLength": 1000 } } }
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
                                              { "from": "classify", "to": "research", "condition": { "path": "output.kind", "op": "eq", "value": "feature" } },
                                              { "from": "classify", "to": "triage", "condition": { "path": "output.kind", "op": "eq", "value": "bug" } },
                                              { "from": "classify", "to": "answer", "condition": { "path": "output.kind", "op": "eq", "value": "question" } },
                                              { "from": "classify", "to": "needsinfo", "condition": { "path": "output.kind", "op": "eq", "value": "unclear" } },
                                              { "from": "research", "to": "plan" },
                                              { "from": "plan", "to": "planapproval" },
                                              { "from": "triage", "to": "rootcause" },
                                              { "from": "rootcause", "to": "fixplan" },
                                              { "from": "fixplan", "to": "fixapproval" },
                                              { "from": "planapproval", "to": "approved", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                              { "from": "fixapproval", "to": "approved", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                              { "from": "approved", "to": "decompose" },
                                              { "from": "approved", "to": "verify" },
                                              { "from": "decompose", "to": "join" },
                                              { "from": "implement", "to": "validate" },
                                              { "from": "validate", "to": "join" },
                                              { "from": "join", "to": "verify" },
                                              { "from": "verify", "to": "integrationapproval" },
                                              { "from": "integrationapproval", "to": "integrate", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                              { "from": "integrate", "to": "fullvalidate" }
                                            ]
                                          }
                                          """;

    /// <summary>
    ///     The <c>dev-intake-v1</c> graph the first build shipped, kept so an installation still holding it verbatim can be
    ///     brought up to the current one, under the same rule as <see cref="FeatureDevelopmentGraphRevision1" />.
    /// </summary>
    /// <remarks>
    ///     Its verification node was told to judge "what each task produced" when no patch reached it. Editing the live
    ///     template means copying it here first, byte for byte.
    /// </remarks>
    internal const string DevIntakeGraphRevision1 = $$"""
                                                      {
                                                        "schemaVersion": 1,
                                                        "nodes": [
                                                          {
                                                            "nodeKey": "classify",
                                                            "nodeType": "Agent",
                                                            "label": "Classify the request",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                            "instructions": "Classify the operator's request so the run takes the right path. Read enough of this repository to tell where the work would land, and do not plan, fix or answer anything here. kind: \"feature\" for new behaviour or a change to existing behaviour (a change request IS a feature), \"bug\" for behaviour that is wrong today, \"question\" for a request that wants an explanation and no change, and \"unclear\" when the request cannot be acted on as written: it names no behaviour, contradicts itself, or leaves out what anyone would have to ask first. area: where the work lands. severity and environment describe a bug and are \"unknown\" for anything else. evidenceQuality: \"none\", \"partial\" (a symptom without steps, logs or a failing case) or \"good\" (steps, logs or a failing case). rationale: one or two sentences on why this kind. Before you complete this session you MUST call save_artifact exactly once, with name \"classification.md\", mediaType \"text/markdown\", kind \"Report\", and every field you hand in as output together with your rationale as text: the nodes after you read that artifact, not your output.",
                                                            "outputSchema": { "type": "object", "required": ["kind"], "additionalProperties": false, "properties": { "kind": { "type": "string", "enum": ["feature", "bug", "question", "unclear"] }, "area": { "type": "string", "enum": ["frontend", "backend", "fullstack", "infra", "unknown"] }, "severity": { "type": "string", "enum": ["cosmetic", "minor", "major", "critical", "unknown"] }, "environment": { "type": "string", "enum": ["production", "development", "unknown"] }, "evidenceQuality": { "type": "string", "enum": ["none", "partial", "good"] }, "rationale": { "type": "string", "maxLength": 1000 } } }
                                                          },
                                                          {
                                                            "nodeKey": "answer",
                                                            "nodeType": "Agent",
                                                            "label": "Answer the question",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                            "instructions": "Answer the operator's question from this repository. Read the code, configuration and documentation the question is about and ground every statement in something you actually read, naming the files and symbols. Change nothing: this path ends with your answer. If the repository does not hold the answer, say so plainly and say what you looked at. Before you complete this session you MUST call save_artifact exactly once, with name \"answer.md\", mediaType \"text/markdown\", kind \"Report\", and the whole answer as text."
                                                          },
                                                          {
                                                            "nodeKey": "needsinfo",
                                                            "nodeType": "Agent",
                                                            "label": "Say what is missing",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                            "instructions": "The request was classified as unclear, so this run ends with a note to the operator rather than a guess. Say what is missing before anyone can act on it: the behaviour wanted or the behaviour observed, where it happens, how to reproduce it, the result expected, and anything the classification names. Make each point a question the operator can answer in a line or two, and say what kind of request (feature, bug or question) it would become once answered, if you can tell. Do not research, plan or change anything. Before you complete this session you MUST call save_artifact exactly once, with name \"what-is-missing.md\", mediaType \"text/markdown\", kind \"Report\", and the whole note as text. The operator adds what it names to the work item and starts a new run."
                                                          },
                                                          {
                                                            "nodeKey": "research",
                                                            "nodeType": "Agent",
                                                            "label": "Research",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
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
                                                            "nodeKey": "triage",
                                                            "nodeType": "Agent",
                                                            "label": "Triage",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                            "instructions": "Triage the reported bug, starting from the classification before you: its area says where to look, its environment whether this is happening in production or in development, and its evidence quality how far the report can be trusted. Restate the symptom, the behaviour expected instead and how to reproduce it, then locate the code the area points at and confirm the reported path exists there. When the evidence is too thin to reproduce or locate the fault (evidence quality \"none\", or a symptom nothing in the repository explains), do not guess: write down exactly what is missing, save the artifact, and complete the session with objectiveMet false, so a person is asked instead of a fix being planned on a guess. Before you complete this session you MUST call save_artifact exactly once, with name \"triage.md\", mediaType \"text/markdown\", kind \"Report\", and the whole triage as text."
                                                          },
                                                          {
                                                            "nodeKey": "rootcause",
                                                            "nodeType": "Agent",
                                                            "label": "Find the root cause",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                            "instructions": "Find the root cause of the triaged bug in this repository. Start from the code the triage located and trace the faulty behaviour back to where it originates, not where it shows, and check every caller of the code you suspect: a cause in a shared path breaks all of them. Ground every claim in something you actually read, naming the files and symbols. If you cannot reach a cause you would stake a fix on, say what you ruled out and what you would need, and complete the session with objectiveMet false. Before you complete this session you MUST call save_artifact exactly once, with name \"rootcause.md\", mediaType \"text/markdown\", kind \"Report\", and the whole analysis as text."
                                                          },
                                                          {
                                                            "nodeKey": "fixplan",
                                                            "nodeType": "Agent",
                                                            "label": "Plan the fix",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                            "instructions": "Turn the root cause into a fix plan a person can approve: the change that removes the cause rather than hiding the symptom, where it goes, the test that fails before the fix and passes after it, and what else the change could affect. Before you complete this session you MUST call save_artifact exactly once, with name \"fix-plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
                                                          },
                                                          {
                                                            "nodeKey": "fixapproval",
                                                            "nodeType": "HumanGate",
                                                            "label": "Approve the fix plan"
                                                          },
                                                          {
                                                            "nodeKey": "approved",
                                                            "nodeType": "Join",
                                                            "joinPolicy": "Any",
                                                            "label": "An approved plan"
                                                          },
                                                          {
                                                            "nodeKey": "decompose",
                                                            "nodeType": "Agent",
                                                            "label": "Decompose",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                            "instructions": "This run took one of two paths, a feature plan or a bug-fix plan, and the steps of the path it did not take are listed under \"Skipped steps\": that is the branch not taken, expected and not missing work, so do not report it. Split the approved plan into implementation tasks. Default to ONE task: a request that names one method, one file or one behaviour is a single task that implements it and adds its test file together, and splitting it only buys failures. Split only where the slices are independent features a build can judge separately; ten tasks is the ceiling. Every task must change code — never emit a survey, a read-only investigation or a verify-only slice, and never chain a \"build and verify\" task, because validation runs automatically after every task. Tests a task adds go in a NEW test file named in \"changes\"; a test file that already exists may not be edited. Before you complete this session you MUST call save_artifact exactly once, with name \"tasks.json\", mediaType \"application/json\", kind \"Report\", and a JSON array as the text: [{\"id\": \"short-slug\", \"title\": \"what it is\", \"goal\": \"what to implement, in full — this becomes the task's requirements and it is all the coder is told\", \"changes\": [\"src/…\", \"tests/…NewFile.cs\"], \"dependsOn\": [\"another-id\"], \"acceptanceCriteria\": [\"how to tell it is done\"]}]. Ids must be unique, dependsOn may only name ids in this same array, and there must be no dependency cycle. \"changes\" must name at least one workspace-relative file the task will add or edit. If the plan needs no implementation work, answer with an empty array rather than inventing one.",
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
                                                            "instructions": "Judge the implemented slices against the approved plan, independently: read what each task produced and say whether the planned change is actually there, what is missing, and what you would not sign off. For a bug-fix plan, say whether the change removes the root cause the plan names and whether a test now shows it. This run took one of two paths, a feature plan or a bug-fix plan, and the steps of the path it did not take are listed under \"Skipped steps\": that is the branch not taken, expected and not missing work, so do not report it. A skipped implementation slice is different: that work did not arrive, so judge without it and say what it leaves undone. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text."
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
                                                          { "from": "classify", "to": "research", "condition": { "path": "output.kind", "op": "eq", "value": "feature" } },
                                                          { "from": "classify", "to": "triage", "condition": { "path": "output.kind", "op": "eq", "value": "bug" } },
                                                          { "from": "classify", "to": "answer", "condition": { "path": "output.kind", "op": "eq", "value": "question" } },
                                                          { "from": "classify", "to": "needsinfo", "condition": { "path": "output.kind", "op": "eq", "value": "unclear" } },
                                                          { "from": "research", "to": "plan" },
                                                          { "from": "plan", "to": "planapproval" },
                                                          { "from": "triage", "to": "rootcause" },
                                                          { "from": "rootcause", "to": "fixplan" },
                                                          { "from": "fixplan", "to": "fixapproval" },
                                                          { "from": "planapproval", "to": "approved", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                          { "from": "fixapproval", "to": "approved", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                          { "from": "approved", "to": "decompose" },
                                                          { "from": "approved", "to": "verify" },
                                                          { "from": "decompose", "to": "join" },
                                                          { "from": "implement", "to": "validate" },
                                                          { "from": "validate", "to": "join" },
                                                          { "from": "join", "to": "verify" },
                                                          { "from": "verify", "to": "integrationapproval" },
                                                          { "from": "integrationapproval", "to": "integrate", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                          { "from": "integrate", "to": "fullvalidate" }
                                                        ]
                                                      }
                                                      """;

    /// <summary>
    ///     The revision this build replaces: <see cref="DevIntakeGraphRevision1" /> with verify judging the published patches, paged
    ///     through <c>read_upstream_artifact</c>.
    /// </summary>
    /// <remarks>
    ///     Its verify could close met while refusing a task in its report, and say a patch was read in full when it was not.
    ///     Kept for the same reason revision 1 is.
    /// </remarks>
    internal const string DevIntakeGraphRevision2 = $$"""
                                                      {
                                                        "schemaVersion": 1,
                                                        "nodes": [
                                                          {
                                                            "nodeKey": "classify",
                                                            "nodeType": "Agent",
                                                            "label": "Classify the request",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                            "instructions": "Classify the operator's request so the run takes the right path. Read enough of this repository to tell where the work would land, and do not plan, fix or answer anything here. kind: \"feature\" for new behaviour or a change to existing behaviour (a change request IS a feature), \"bug\" for behaviour that is wrong today, \"question\" for a request that wants an explanation and no change, and \"unclear\" when the request cannot be acted on as written: it names no behaviour, contradicts itself, or leaves out what anyone would have to ask first. area: where the work lands. severity and environment describe a bug and are \"unknown\" for anything else. evidenceQuality: \"none\", \"partial\" (a symptom without steps, logs or a failing case) or \"good\" (steps, logs or a failing case). rationale: one or two sentences on why this kind. Before you complete this session you MUST call save_artifact exactly once, with name \"classification.md\", mediaType \"text/markdown\", kind \"Report\", and every field you hand in as output together with your rationale as text: the nodes after you read that artifact, not your output.",
                                                            "outputSchema": { "type": "object", "required": ["kind"], "additionalProperties": false, "properties": { "kind": { "type": "string", "enum": ["feature", "bug", "question", "unclear"] }, "area": { "type": "string", "enum": ["frontend", "backend", "fullstack", "infra", "unknown"] }, "severity": { "type": "string", "enum": ["cosmetic", "minor", "major", "critical", "unknown"] }, "environment": { "type": "string", "enum": ["production", "development", "unknown"] }, "evidenceQuality": { "type": "string", "enum": ["none", "partial", "good"] }, "rationale": { "type": "string", "maxLength": 1000 } } }
                                                          },
                                                          {
                                                            "nodeKey": "answer",
                                                            "nodeType": "Agent",
                                                            "label": "Answer the question",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                            "instructions": "Answer the operator's question from this repository. Read the code, configuration and documentation the question is about and ground every statement in something you actually read, naming the files and symbols. Change nothing: this path ends with your answer. If the repository does not hold the answer, say so plainly and say what you looked at. Before you complete this session you MUST call save_artifact exactly once, with name \"answer.md\", mediaType \"text/markdown\", kind \"Report\", and the whole answer as text."
                                                          },
                                                          {
                                                            "nodeKey": "needsinfo",
                                                            "nodeType": "Agent",
                                                            "label": "Say what is missing",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                            "instructions": "The request was classified as unclear, so this run ends with a note to the operator rather than a guess. Say what is missing before anyone can act on it: the behaviour wanted or the behaviour observed, where it happens, how to reproduce it, the result expected, and anything the classification names. Make each point a question the operator can answer in a line or two, and say what kind of request (feature, bug or question) it would become once answered, if you can tell. Do not research, plan or change anything. Before you complete this session you MUST call save_artifact exactly once, with name \"what-is-missing.md\", mediaType \"text/markdown\", kind \"Report\", and the whole note as text. The operator adds what it names to the work item and starts a new run."
                                                          },
                                                          {
                                                            "nodeKey": "research",
                                                            "nodeType": "Agent",
                                                            "label": "Research",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
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
                                                            "nodeKey": "triage",
                                                            "nodeType": "Agent",
                                                            "label": "Triage",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                            "instructions": "Triage the reported bug, starting from the classification before you: its area says where to look, its environment whether this is happening in production or in development, and its evidence quality how far the report can be trusted. Restate the symptom, the behaviour expected instead and how to reproduce it, then locate the code the area points at and confirm the reported path exists there. When the evidence is too thin to reproduce or locate the fault (evidence quality \"none\", or a symptom nothing in the repository explains), do not guess: write down exactly what is missing, save the artifact, and complete the session with objectiveMet false, so a person is asked instead of a fix being planned on a guess. Before you complete this session you MUST call save_artifact exactly once, with name \"triage.md\", mediaType \"text/markdown\", kind \"Report\", and the whole triage as text."
                                                          },
                                                          {
                                                            "nodeKey": "rootcause",
                                                            "nodeType": "Agent",
                                                            "label": "Find the root cause",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionRepositoryResearchAgentSeedSlug}}",
                                                            "instructions": "Find the root cause of the triaged bug in this repository. Start from the code the triage located and trace the faulty behaviour back to where it originates, not where it shows, and check every caller of the code you suspect: a cause in a shared path breaks all of them. Ground every claim in something you actually read, naming the files and symbols. If you cannot reach a cause you would stake a fix on, say what you ruled out and what you would need, and complete the session with objectiveMet false. Before you complete this session you MUST call save_artifact exactly once, with name \"rootcause.md\", mediaType \"text/markdown\", kind \"Report\", and the whole analysis as text."
                                                          },
                                                          {
                                                            "nodeKey": "fixplan",
                                                            "nodeType": "Agent",
                                                            "label": "Plan the fix",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                            "instructions": "Turn the root cause into a fix plan a person can approve: the change that removes the cause rather than hiding the symptom, where it goes, the test that fails before the fix and passes after it, and what else the change could affect. Before you complete this session you MUST call save_artifact exactly once, with name \"fix-plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
                                                          },
                                                          {
                                                            "nodeKey": "fixapproval",
                                                            "nodeType": "HumanGate",
                                                            "label": "Approve the fix plan"
                                                          },
                                                          {
                                                            "nodeKey": "approved",
                                                            "nodeType": "Join",
                                                            "joinPolicy": "Any",
                                                            "label": "An approved plan"
                                                          },
                                                          {
                                                            "nodeKey": "decompose",
                                                            "nodeType": "Agent",
                                                            "label": "Decompose",
                                                            "agentSeedSlug": "{{AgentDefaults.WorkSessionGeneralAgentSeedSlug}}",
                                                            "instructions": "This run took one of two paths, a feature plan or a bug-fix plan, and the steps of the path it did not take are listed under \"Skipped steps\": that is the branch not taken, expected and not missing work, so do not report it. Split the approved plan into implementation tasks. Default to ONE task: a request that names one method, one file or one behaviour is a single task that implements it and adds its test file together, and splitting it only buys failures. Split only where the slices are independent features a build can judge separately; ten tasks is the ceiling. Every task must change code — never emit a survey, a read-only investigation or a verify-only slice, and never chain a \"build and verify\" task, because validation runs automatically after every task. Tests a task adds go in a NEW test file named in \"changes\"; a test file that already exists may not be edited. Before you complete this session you MUST call save_artifact exactly once, with name \"tasks.json\", mediaType \"application/json\", kind \"Report\", and a JSON array as the text: [{\"id\": \"short-slug\", \"title\": \"what it is\", \"goal\": \"what to implement, in full — this becomes the task's requirements and it is all the coder is told\", \"changes\": [\"src/…\", \"tests/…NewFile.cs\"], \"dependsOn\": [\"another-id\"], \"acceptanceCriteria\": [\"how to tell it is done\"]}]. Ids must be unique, dependsOn may only name ids in this same array, and there must be no dependency cycle. \"changes\" must name at least one workspace-relative file the task will add or edit. If the plan needs no implementation work, answer with an empty array rather than inventing one.",
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
                                                            "instructions": "Judge the implemented slices against the approved plan (plan.md for a feature, fix-plan.md for a bug fix), independently, from the patches themselves. Each task's validation step publishes the patch it tested as an artifact named after that step with .patch on the end (validate#<task id>.patch), beside that task's validation report: judge each patch against the plan and that report, and say whether the planned change is actually there, what is missing, and what you would not sign off. For a bug-fix plan, say whether the change removes the root cause the plan names and whether a test now shows it. A patch shown only in part, or by its header alone, says so: call read_upstream_artifact with its name, and again with the nextOffset it hands back, until you have read the whole patch, and sign off only on what you actually read. When a task's validation report says its patch was not published, you cannot read that change: say so and why, do not sign it off blind, and complete the session with objectiveMet false naming that task. This run took one of two paths, a feature plan or a bug-fix plan, and the steps of the path it did not take are listed under \"Skipped steps\": that is the branch not taken, expected and not missing work, so do not report it. A skipped implementation slice is different: that work did not arrive, so judge without it and say what it leaves undone. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text."
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
                                                          { "from": "classify", "to": "research", "condition": { "path": "output.kind", "op": "eq", "value": "feature" } },
                                                          { "from": "classify", "to": "triage", "condition": { "path": "output.kind", "op": "eq", "value": "bug" } },
                                                          { "from": "classify", "to": "answer", "condition": { "path": "output.kind", "op": "eq", "value": "question" } },
                                                          { "from": "classify", "to": "needsinfo", "condition": { "path": "output.kind", "op": "eq", "value": "unclear" } },
                                                          { "from": "research", "to": "plan" },
                                                          { "from": "plan", "to": "planapproval" },
                                                          { "from": "triage", "to": "rootcause" },
                                                          { "from": "rootcause", "to": "fixplan" },
                                                          { "from": "fixplan", "to": "fixapproval" },
                                                          { "from": "planapproval", "to": "approved", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                          { "from": "fixapproval", "to": "approved", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                          { "from": "approved", "to": "decompose" },
                                                          { "from": "approved", "to": "verify" },
                                                          { "from": "decompose", "to": "join" },
                                                          { "from": "implement", "to": "validate" },
                                                          { "from": "validate", "to": "join" },
                                                          { "from": "join", "to": "verify" },
                                                          { "from": "verify", "to": "integrationapproval" },
                                                          { "from": "integrationapproval", "to": "integrate", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                          { "from": "integrate", "to": "fullvalidate" }
                                                        ]
                                                      }
                                                      """;

    /// <summary>
    ///     Every <c>feature-development-v1</c> graph this build knows it published, the list an untouched row is
    ///     recognised by.
    /// </summary>
    /// <remarks>
    ///     Hoisted out of the call site so a test can assert a kept revision is actually IN it: a copy that never
    ///     reaches <see cref="SeedAsync" /> looks right in the file and silently strands every installation on it.
    /// </remarks>
    internal static readonly string[] FeatureDevelopmentPriorRevisions =
        [
            FeatureDevelopmentGraphRevision1, FeatureDevelopmentGraphRevision2, FeatureDevelopmentGraphRevision3, FeatureDevelopmentGraphRevision4,
            FeatureDevelopmentGraphRevision5
        ];

    /// <summary>Every <c>dev-intake-v1</c> graph this build knows it published, hoisted for the same reason.</summary>
    internal static readonly string[] DevIntakePriorRevisions = [DevIntakeGraphRevision1, DevIntakeGraphRevision2];

    private readonly ILogger<DevWorkflowDefinitionSeeder> _logger;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IServiceScopeFactory _scopeFactory;

    public DevWorkflowDefinitionSeeder(IServiceScopeFactory scopeFactory,
        INodeRuntimeSettings runtimeSettings,
        ILogger<DevWorkflowDefinitionSeeder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Read once at start: a stored change seeds on the next restart.
        if (!await _runtimeSettings.GetDevWorkflowsEnabledAsync(cancellationToken))
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
            await SeedAsync(store, ResearchPlanApprovalSlug, ResearchPlanApprovalName, ResearchPlanApprovalGraph, [], cancellationToken);
            await SeedAsync(store, FeatureDevelopmentSlug, FeatureDevelopmentName, FeatureDevelopmentGraph, FeatureDevelopmentPriorRevisions, cancellationToken);
            await SeedAsync(store, DevIntakeSlug, DevIntakeName, DevIntakeGraph, DevIntakePriorRevisions, cancellationToken);
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
        var existing = (await store.ListDefinitionsAsync(includeArchived: true, cancellationToken))
            .FirstOrDefault(definition => string.Equals(definition.SeedSlug, seedSlug, StringComparison.Ordinal));

        // The store hashes a definition's graph bytes at every save, so this answers "is this row the graph this build ships" without decrypting a blob — and it is the graph BYTES on
        // both sides, the right comparison precisely because an untouched seeded row holds a shipped constant verbatim.
        if (existing is not null && string.Equals(existing.GraphHash, GraphHash(graphJson), StringComparison.Ordinal))
        {
            return;
        }

        // Untouched is "this row is a graph WE shipped", not "this row has never been written": reading it off the version would let the seeder's own upgrade make every later template
        // change look like an operator's edit. Matching content against the published revisions still cannot mistake an operator's graph for ours — they would have typed it byte for byte.
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
            var seeded = await store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand
                {
                    DefinitionId = Guid.NewGuid(),
                    Name = name,
                    GraphJson = graphJson,
                    NodeCount = graph.Nodes.Count,
                    Source = DevWorkflowDefinitionSource.Seeded,
                    SeedSlug = seedSlug
                },
                cancellationToken);
            _logger.LogInformation("Seeded the {Name} workflow definition {DefinitionId} (slug {SeedSlug}).", name, seeded.Id, seedSlug);
            return;
        }

        // The GRAPH only. A name is the operator's to choose — renaming a seeded template is a name-only PUT that leaves the graph one of ours, so it still qualifies for the catch-up,
        // and passing the shipped name here would revert their label as a side effect of a fix they never asked about.
        var upgraded = await store.UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand
            {
                DefinitionId = existing.Id,
                ExpectedVersion = existing.Version,
                Name = null,
                GraphJson = graphJson,
                NodeCount = graph.Nodes.Count
            },
            cancellationToken);
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
