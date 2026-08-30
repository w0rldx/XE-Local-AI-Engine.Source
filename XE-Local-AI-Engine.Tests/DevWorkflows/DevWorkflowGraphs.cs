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

    /// <summary>
    ///     A terminal human gate — no out-edges at all. This is the seeded "Research → Plan → Approval" shape reduced to
    ///     its last node, which is why a rejection here has to be handled rather than treated as a corner case.
    /// </summary>
    public const string TerminalGate = """
                                       {
                                         "schemaVersion": 1,
                                         "nodes": [{ "nodeKey": "approve", "nodeType": "HumanGate", "label": "Approve" }],
                                         "edges": []
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

    /// <summary>A fan-out whose two branches both stall on the same thing, so one can be poisoned and the other watched.</summary>
    public const string TwoStalledSiblings = """
                                             {
                                               "schemaVersion": 1,
                                               "nodes": [
                                                 { "nodeKey": "start", "nodeType": "Parallel" },
                                                 { "nodeKey": "left", "nodeType": "Agent" },
                                                 { "nodeKey": "right", "nodeType": "Agent" },
                                                 { "nodeKey": "join", "nodeType": "Join" }
                                               ],
                                               "edges": [
                                                 { "from": "start", "to": "left" },
                                                 { "from": "start", "to": "right" },
                                                 { "from": "left", "to": "join" },
                                                 { "from": "right", "to": "join" }
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

    /// <summary>
    ///     The same fan-out as a shape the runtime can actually drive today: the implementation node is an Agent because
    ///     the DevTask lane arrives in B6, and everything the fix loop is specified against — one producer, two checks
    ///     that both depend on it, a join, and the failing check routing back — is unchanged.
    /// </summary>
    public const string FanOutFixLoop = """
                                        {
                                          "schemaVersion": 1,
                                          "nodes": [
                                            { "nodeKey": "implement", "nodeType": "Agent", "label": "Implement",
                                              "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
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

    /// <summary>
    ///     TWO checks that both route their failures to the same producer, so both can fail in one round and each is a
    ///     node the other's reset would move. The shape a fix loop deadlocks on if a route waits for its siblings.
    /// </summary>
    public const string TwoChecksBothRoutingBack = """
                                                   {
                                                     "schemaVersion": 1,
                                                     "nodes": [
                                                       { "nodeKey": "implement", "nodeType": "Agent", "label": "Implement",
                                                         "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                       { "nodeKey": "checkone", "nodeType": "Tool", "retryTarget": "implement" },
                                                       { "nodeKey": "checktwo", "nodeType": "Tool", "retryTarget": "implement" },
                                                       { "nodeKey": "join", "nodeType": "Join" }
                                                     ],
                                                     "edges": [
                                                       { "from": "implement", "to": "checkone" },
                                                       { "from": "implement", "to": "checktwo" },
                                                       { "from": "checkone", "to": "join" },
                                                       { "from": "checktwo", "to": "join" }
                                                     ]
                                                   }
                                                   """;

    /// <summary>
    ///     A check that routes its failure upstream while a human gate on the OTHER branch is still open — the one shape
    ///     in which the reset has to move a node run out of a durable human wait.
    /// </summary>
    public const string FixLoopBesideAnOpenGate = """
                                                  {
                                                    "schemaVersion": 1,
                                                    "nodes": [
                                                      { "nodeKey": "implement", "nodeType": "Agent", "label": "Implement",
                                                        "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                      { "nodeKey": "approve", "nodeType": "HumanGate" },
                                                      { "nodeKey": "test", "nodeType": "Tool", "retryTarget": "implement" },
                                                      { "nodeKey": "join", "nodeType": "Join", "joinPolicy": "Any" }
                                                    ],
                                                    "edges": [
                                                      { "from": "implement", "to": "approve" },
                                                      { "from": "implement", "to": "test" },
                                                      { "from": "approve", "to": "join" },
                                                      { "from": "test", "to": "join" }
                                                    ]
                                                  }
                                                  """;

    /// <summary>
    ///     A chain whose last node routes its failure back to the first, so the re-run passes back through a node that
    ///     has already produced an artifact and through the node that CONSUMED that artifact and produced one of its own.
    ///     The only shape in which superseding and the staleness that follows it are both observable.
    /// </summary>
    public const string FixLoopOverAConsumedArtifact = """
                                                       {
                                                         "schemaVersion": 1,
                                                         "nodes": [
                                                           { "nodeKey": "validate", "nodeType": "Tool" },
                                                           { "nodeKey": "summarize", "nodeType": "Agent", "label": "Summarize",
                                                             "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                           { "nodeKey": "verify", "nodeType": "Tool", "retryTarget": "validate" }
                                                         ],
                                                         "edges": [
                                                           { "from": "validate", "to": "summarize" },
                                                           { "from": "summarize", "to": "verify" }
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

    /// <summary>One tool node on its own: the smallest thing the sandbox lane can be asked to run.</summary>
    public const string SingleTool = """
                                     {
                                       "schemaVersion": 1,
                                       "nodes": [{ "nodeKey": "validate", "nodeType": "Tool", "label": "Validate" }],
                                       "edges": []
                                     }
                                     """;

    /// <summary>Two tool nodes admitted by the same tick, so the lane's slot count is observable rather than inferred.</summary>
    public const string TwoParallelTools = """
                                           {
                                             "schemaVersion": 1,
                                             "nodes": [
                                               { "nodeKey": "start", "nodeType": "Parallel" },
                                               { "nodeKey": "first", "nodeType": "Tool" },
                                               { "nodeKey": "second", "nodeType": "Tool" },
                                               { "nodeKey": "join", "nodeType": "Join" }
                                             ],
                                             "edges": [
                                               { "from": "start", "to": "first" },
                                               { "from": "start", "to": "second" },
                                               { "from": "first", "to": "join" },
                                               { "from": "second", "to": "join" }
                                             ]
                                           }
                                           """;

    /// <summary>
    ///     A human gate beside sandbox work that is genuinely in flight — the X10 case as it actually occurs: one branch
    ///     is mid-build at the moment the other's approval is refused.
    /// </summary>
    public const string GateBesideSandboxWork = """
                                                {
                                                  "schemaVersion": 1,
                                                  "nodes": [
                                                    { "nodeKey": "start", "nodeType": "Parallel" },
                                                    { "nodeKey": "approve", "nodeType": "HumanGate" },
                                                    { "nodeKey": "validate", "nodeType": "Tool" },
                                                    { "nodeKey": "done", "nodeType": "Join", "joinPolicy": "Any" }
                                                  ],
                                                  "edges": [
                                                    { "from": "start", "to": "approve" },
                                                    { "from": "start", "to": "validate" },
                                                    { "from": "approve", "to": "done", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                    { "from": "validate", "to": "done" }
                                                  ]
                                                }
                                                """;

    /// <summary>
    ///     An automatic gate routing on the document its upstream produced. The human gate above it takes every answer,
    ///     so which way the run goes is decided by the Gate's own conditions and by nothing else.
    /// </summary>
    public const string GateOnADecision = """
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              { "nodeKey": "approve", "nodeType": "HumanGate" },
                                              { "nodeKey": "choose", "nodeType": "Gate" },
                                              { "nodeKey": "ship", "nodeType": "Join" },
                                              { "nodeKey": "revise", "nodeType": "Join" }
                                            ],
                                            "edges": [
                                              { "from": "approve", "to": "choose" },
                                              { "from": "choose", "to": "ship", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                              { "from": "choose", "to": "revise", "condition": { "path": "decision", "op": "eq", "value": "RequestChanges" } }
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
