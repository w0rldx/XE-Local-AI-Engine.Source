namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Services.Agents;

public sealed partial class DevWorkflowDefinitionSeeder
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
}
