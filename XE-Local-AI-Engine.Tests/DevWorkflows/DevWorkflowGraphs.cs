namespace XE_Local_AI_Engine.Tests.DevWorkflows;

/// <summary>The graph fixtures the runtime suites route over, named for the shape rather than for the test that uses them.</summary>
internal static class DevWorkflowGraphs
{
    /// <summary>The Slice A shape: strictly linear, no repository needed, ending on a human gate.</summary>
    public const string ResearchPlanApproval = """
                                               {
                                                 "schemaVersion": 1,
                                                 "nodes": [
                                                   { "nodeKey": "research", "nodeType": "Agent", "label": "Research" },
                                                   { "nodeKey": "plan", "nodeType": "Agent", "label": "Plan" },
                                                   { "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve the plan" }
                                                 ],
                                                 "edges": [
                                                   { "from": "research", "to": "plan" },
                                                   { "from": "plan", "to": "approve" }
                                                 ]
                                               }
                                               """;

    /// <summary>A gate with two mutually exclusive out-edges reconverging on an <c>Any</c> join.</summary>
    public const string ApprovalBranches = """
                                           {
                                             "schemaVersion": 1,
                                             "nodes": [
                                               { "nodeKey": "approve", "nodeType": "HumanGate" },
                                               { "nodeKey": "ship", "nodeType": "Tool" },
                                               { "nodeKey": "revise", "nodeType": "Agent" },
                                               { "nodeKey": "done", "nodeType": "Join", "joinPolicy": "Any" }
                                             ],
                                             "edges": [
                                               { "from": "approve", "to": "ship", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                               { "from": "approve", "to": "revise", "condition": { "path": "decision", "op": "eq", "value": "RequestChanges" } },
                                               { "from": "ship", "to": "done" },
                                               { "from": "revise", "to": "done" }
                                             ]
                                           }
                                           """;

    /// <summary>The fan-out the cross-node fix loop is specified against: one implementation, two checks, one join.</summary>
    public const string FanOut = """
                                 {
                                   "schemaVersion": 1,
                                   "nodes": [
                                     { "nodeKey": "implement", "nodeType": "DevTask" },
                                     { "nodeKey": "lint", "nodeType": "Tool" },
                                     { "nodeKey": "test", "nodeType": "Tool", "retryTarget": "implement" },
                                     { "nodeKey": "join", "nodeType": "Join" }
                                   ],
                                   "edges": [
                                     { "from": "implement", "to": "lint" },
                                     { "from": "implement", "to": "test" },
                                     { "from": "lint", "to": "join" },
                                     { "from": "test", "to": "join" }
                                   ]
                                 }
                                 """;

    /// <summary>A decomposing node whose template is unreachable on purpose.</summary>
    public const string Decomposition = """
                                        {
                                          "schemaVersion": 1,
                                          "nodes": [
                                            { "nodeKey": "decompose", "nodeType": "Agent",
                                              "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 20 } },
                                            { "nodeKey": "implement", "nodeType": "DevTask" },
                                            { "nodeKey": "join", "nodeType": "Join" }
                                          ],
                                          "edges": [
                                            { "from": "decompose", "to": "join" }
                                          ]
                                        }
                                        """;

    /// <summary>Three levels of a single chain, for asserting that a skip propagates all the way down.</summary>
    public const string ThreeLevelChain = """
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              { "nodeKey": "gate", "nodeType": "Gate" },
                                              { "nodeKey": "first", "nodeType": "Tool" },
                                              { "nodeKey": "second", "nodeType": "Tool" },
                                              { "nodeKey": "third", "nodeType": "Tool" }
                                            ],
                                            "edges": [
                                              { "from": "gate", "to": "first", "condition": { "path": "passed", "op": "eq", "value": true } },
                                              { "from": "first", "to": "second" },
                                              { "from": "second", "to": "third" }
                                            ]
                                          }
                                          """;
}
