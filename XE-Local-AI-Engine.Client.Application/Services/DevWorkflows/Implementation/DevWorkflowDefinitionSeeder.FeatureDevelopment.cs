namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Services.Agents;

public sealed partial class DevWorkflowDefinitionSeeder
{
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
                                                            "instructions": "Turn the research into a plan a person can approve: what to build, in what order, and what would show it worked. The operator's request is the workItemRequest under \"What was asked\": keep every name, signature and interface it specifies exactly as it is written, and do not rename, merge or replace any of them on the research's say-so. Where the plan has to deviate from the request, list each deviation and why under a heading \"Deviations from the request\", and write \"None\" there when there is none. Before you complete this session you MUST call save_artifact exactly once, with name \"plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
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
                                                            "instructions": "Judge the implemented slices independently, from the patches themselves: against the operator's request first (the workItemRequest under \"What was asked\"), then against the approved plan (plan.md). The request wins. A deviation the approved plan lists under \"Deviations from the request\" was approved with it; any other place where a patch drops, renames or changes a name, signature or interface the request specifies is a task you refuse, even when the patch matches the plan. Each task's validation step publishes the patch it tested as an artifact named after that step with .patch on the end (validate#<task id>.patch), beside that task's validation report: judge each patch against the request, the plan and that report, and say whether the feature is actually there, what is missing, and what you would not sign off. Read every patch to its end: call read_upstream_artifact with its name, and again with the nextOffset it hands back, until it answers the end of the artifact, even when the objective already shows the whole patch. The engine records the pages it served you, and a patch you did not read to its end stops the run whatever your report says. When a task's validation report says its patch was not published, you cannot read that change: say so and why, and do not sign it off blind. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text. Then complete it with output verdict \"approved\" only when you sign off every task, and otherwise verdict \"refused\" with refusedTasks listing the ids of every task you would not sign off: a refused verdict stops the run for a person, whatever objectiveMet says.",
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
    ///     editing the live template means copying it here FIRST, byte for byte, or the catch-up stops recognizing
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
    ///     Revision 5: <see cref="FeatureDevelopmentGraphRevision4" /> with verify judging the published patches,
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
    ///     The revision this build replaces: <see cref="FeatureDevelopmentGraphRevision5" /> with verify handing in a verdict.
    /// </summary>
    /// <remarks>
    ///     Its plan and verify never saw the request, so a plan that renamed what the request named was judged against itself.
    ///     Kept for the same reason revision 1 is.
    /// </remarks>
    internal const string FeatureDevelopmentGraphRevision6 = $$"""
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
    ///     Every <c>feature-development-v1</c> graph this build knows it published, the list an untouched row is
    ///     recognized by.
    /// </summary>
    /// <remarks>
    ///     Hoisted out of the call site so a test can assert a kept revision is actually IN it: a copy that never
    ///     reaches <see cref="SeedAsync" /> looks right in the file and silently strands every installation on it.
    /// </remarks>
    internal static readonly string[] FeatureDevelopmentPriorRevisions =
    [
        FeatureDevelopmentGraphRevision1, FeatureDevelopmentGraphRevision2, FeatureDevelopmentGraphRevision3, FeatureDevelopmentGraphRevision4,
        FeatureDevelopmentGraphRevision5, FeatureDevelopmentGraphRevision6
    ];
}
