namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Services.Agents;

public sealed partial class DevWorkflowDefinitionSeeder
{
    /// <summary>
    ///     The <c>dev-intake-v1</c> template: a classifier whose <c>output.kind</c> routes a feature or a bug to its own
    ///     plan gate and then one shared tail, a question to an answer, and an unclear request to questions for its requester.
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
                                                   "instructions": "The request was classified as unclear, so this run ends with a note to the operator rather than a guess. Say what is missing before anyone can act on it: the behaviour wanted or the behaviour observed, where it happens, how to reproduce it, the result expected, and anything the classification names. Write each point as a numbered question the requester can answer in a line or two, and say what kind of request (feature, bug or question) it would become once answered, if you can tell. Do not research, plan or change anything. Before you complete this session you MUST call save_artifact exactly once, with name \"questions.md\", mediaType \"text/markdown\", kind \"Report\", and every question as text. This run ends here: the operator puts the answers into the work item's request and starts a new run."
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
                                                   "instructions": "Turn the research into a plan a person can approve: what to build, in what order, and what would show it worked. The operator's request is the workItemRequest under \"What was asked\": keep every name, signature and interface it specifies exactly as it is written, and do not rename, merge or replace any of them on the research's say-so. Where the plan has to deviate from the request, list each deviation and why under a heading \"Deviations from the request\", and write \"None\" there when there is none. Before you complete this session you MUST call save_artifact exactly once, with name \"plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
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
                                                   "instructions": "Turn the root cause into a fix plan a person can approve: the change that removes the cause rather than hiding the symptom, where it goes, the test that fails before the fix and passes after it, and what else the change could affect. The operator's request is the workItemRequest under \"What was asked\": keep every name, signature and interface it specifies exactly as it is written. Where the plan has to deviate from the request, list each deviation and why under a heading \"Deviations from the request\", and write \"None\" there when there is none. Before you complete this session you MUST call save_artifact exactly once, with name \"fix-plan.md\", mediaType \"text/markdown\", kind \"Report\", and the whole plan as text. The approval gate shows the operator that artifact, so a plan left in findings is a plan nobody can approve."
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
                                                   "instructions": "Judge the implemented slices independently, from the patches themselves: against the operator's request first (the workItemRequest under \"What was asked\"), then against the approved plan (plan.md for a feature, fix-plan.md for a bug fix). The request wins. A deviation the approved plan lists under \"Deviations from the request\" was approved with it; any other place where a patch drops, renames or changes a name, signature or interface the request specifies is a task you refuse, even when the patch matches the plan. Each task's validation step publishes the patch it tested as an artifact named after that step with .patch on the end (validate#<task id>.patch), beside that task's validation report: judge each patch against the request, the plan and that report, and say whether the planned change is actually there, what is missing, and what you would not sign off. For a bug-fix plan, say whether the change removes the root cause the plan names and whether a test now shows it. Read every patch to its end: call read_upstream_artifact with its name, and again with the nextOffset it hands back, until it answers the end of the artifact, even when the objective already shows the whole patch. The engine records the pages it served you, and a patch you did not read to its end stops the run whatever your report says. When a task's validation report says its patch was not published, you cannot read that change: say so and why, and do not sign it off blind. This run took one of two paths, a feature plan or a bug-fix plan, and the steps of the path it did not take are listed under \"Skipped steps\": that is the branch not taken, expected and not missing work, so do not report it. A skipped implementation slice is different: that work did not arrive, so judge without it and say what it leaves undone. You are the last reader before an operator is asked to let these patches into the repository, so an honest \"not yet\" is worth more than an approval. Before you complete this session you MUST call save_artifact exactly once, with name \"verification.md\", mediaType \"text/markdown\", kind \"Report\", and your whole assessment as text. Then complete it with output verdict \"approved\" only when you sign off every task, and otherwise verdict \"refused\" with refusedTasks listing the ids of every task you would not sign off: a refused verdict stops the run for a person, whatever objectiveMet says.",
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
    ///     Revision 2: <see cref="DevIntakeGraphRevision1" /> with verify judging the published patches, paged
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
    ///     The revision this build replaces: <see cref="DevIntakeGraphRevision2" /> with verify handing in a verdict.
    /// </summary>
    /// <remarks>
    ///     Its plan and verify never saw the request, and an unclear request ended on a what-is-missing note rather than questions.
    ///     Kept for the same reason revision 1 is.
    /// </remarks>
    internal const string DevIntakeGraphRevision3 = $$"""
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

    /// <summary>Every <c>dev-intake-v1</c> graph this build knows it published, hoisted for the same reason.</summary>
    internal static readonly string[] DevIntakePriorRevisions = [DevIntakeGraphRevision1, DevIntakeGraphRevision2, DevIntakeGraphRevision3];
}
