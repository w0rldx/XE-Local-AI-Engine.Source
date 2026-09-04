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
    ///     <see cref="FanOutFixLoop" /> with the loop BOUNDED by the definition: the check allows one re-run of its
    ///     producer and no more. The producer's own attempt ceiling is raised out of the way, so what stops the loop
    ///     here is the per-loop cap rather than the target's attempts — otherwise the two bounds cannot be told apart.
    /// </summary>
    public const string FanOutFixLoopBounded = """
                                               {
                                                 "schemaVersion": 1,
                                                 "nodes": [
                                                   { "nodeKey": "implement", "nodeType": "Agent", "label": "Implement", "maxAttempts": 6,
                                                     "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                   { "nodeKey": "lint", "nodeType": "Tool" },
                                                   { "nodeKey": "test", "nodeType": "Tool", "retryTarget": "implement", "maxAttempts": 6, "maxLoopIterations": 1 },
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
    ///     A check that routes its failure upstream while BOTH other branches are genuinely working: an agent node
    ///     holding the node's invocation slot and a tool node holding a sandbox slot. The one shape in which the reset
    ///     reaches live lane work that nothing else is coming to settle.
    /// </summary>
    public const string LiveSiblingsBesideAFixLoop = """
                                                     {
                                                       "schemaVersion": 1,
                                                       "nodes": [
                                                         { "nodeKey": "implement", "nodeType": "Agent", "label": "Implement",
                                                           "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                         { "nodeKey": "review", "nodeType": "Agent", "label": "Review",
                                                           "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                         { "nodeKey": "slow", "nodeType": "Tool" },
                                                         { "nodeKey": "check", "nodeType": "Tool", "retryTarget": "implement" },
                                                         { "nodeKey": "join", "nodeType": "Join" }
                                                       ],
                                                       "edges": [
                                                         { "from": "implement", "to": "review" },
                                                         { "from": "implement", "to": "slow" },
                                                         { "from": "implement", "to": "check" },
                                                         { "from": "review", "to": "join" },
                                                         { "from": "slow", "to": "join" },
                                                         { "from": "check", "to": "join" }
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

    /// <summary>
    ///     A producer and the tool node whose commands judge what it made. The smallest shape in which a Tool node has
    ///     anything upstream to record consuming — until it did, the sandbox lane consumed everything and recorded
    ///     nothing.
    /// </summary>
    public const string ToolAfterAProducer = """
                                             {
                                               "schemaVersion": 1,
                                               "nodes": [
                                                 { "nodeKey": "specify", "nodeType": "Agent", "label": "Specify",
                                                   "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                 { "nodeKey": "check", "nodeType": "Tool", "label": "Check" }
                                               ],
                                               "edges": [
                                                 { "from": "specify", "to": "check" }
                                               ]
                                             }
                                             """;

    /// <summary>
    ///     The shipped template's own shape: a materialized DevTask subtree, the verification the join feeds, the
    ///     integration gate, the apply, and a full check past it whose <c>retryTarget</c> reaches back to
    ///     <c>verify</c>. The fix loop that fires once a consumer already exists, which is the only kind whose
    ///     supersessions have anything to flag — and the only shape that can catch a retry target naming a node no run
    ///     instantiates, since <c>implement</c> here is a template key exactly as it is in the seed.
    /// </summary>
    public const string ShippedTailFixLoop = """
                                             {
                                               "schemaVersion": 1,
                                               "nodes": [
                                                 { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose",
                                                   "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b",
                                                   "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                                 { "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "nodeTimeoutSeconds": 900 },
                                                 { "nodeKey": "validate", "nodeType": "Tool", "retryTarget": "implement" },
                                                 { "nodeKey": "join", "nodeType": "Join" },
                                                 { "nodeKey": "verify", "nodeType": "Agent", "label": "Verify",
                                                   "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                 { "nodeKey": "integrationapproval", "nodeType": "HumanGate", "label": "Approve integration" },
                                                 { "nodeKey": "integrate", "nodeType": "Tool", "toolMode": "Apply", "label": "Apply the approved patches" },
                                                 { "nodeKey": "fullvalidate", "nodeType": "Tool", "label": "Validate the integrated result", "retryTarget": "verify" }
                                               ],
                                               "edges": [
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
    ///     A decomposing node whose template is unreachable on purpose. Its template leaf is a <c>DevTask</c>, which
    ///     <c>GRAPH-C4-1</c>'s fourth step does not ask for an out-edge from: no verdict row is ever written under a
    ///     DevTask template key, so it can neither satisfy nor block the completion predicate.
    /// </summary>
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

    /// <summary>
    ///     The §5.10 decomposition shape: the template is a SUBTREE — an implementation and the validation that judges
    ///     it, with the fix loop between them — cloned whole once per task into the join the decomposition names. The
    ///     implementation is an Agent rather than a DevTask so the clones can be driven end to end without a repository;
    ///     what C2 writes for a DevTask child is its input brief, which is asserted on the row.
    /// </summary>
    public const string DecompositionSubtree = """
                                               {
                                                 "schemaVersion": 1,
                                                 "nodes": [
                                                   { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose",
                                                     "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b",
                                                     "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                                   { "nodeKey": "implement", "nodeType": "Agent", "label": "Implement",
                                                     "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                   { "nodeKey": "validate", "nodeType": "Tool", "retryTarget": "implement" },
                                                   { "nodeKey": "join", "nodeType": "Join" }
                                                 ],
                                                 "edges": [
                                                   { "from": "decompose", "to": "join" },
                                                   { "from": "implement", "to": "validate" },
                                                   { "from": "validate", "to": "join" }
                                                 ]
                                               }
                                               """;

    /// <summary>
    ///     <see cref="DecompositionSubtree" /> with the two things the seeded template puts around it that decide what a
    ///     VERIFICATION node gets to read: an approved plan in front of the decomposition, and a verification agent
    ///     behind the join. The seed's own evidence edges are here too — <c>decompose → join</c>, which the materializer
    ///     preserves, and <c>planapproval → verify</c>, which is the only path from the verification back to the plan
    ///     itself, since the walk stops at the decomposition that consumed it.
    ///     <para>
    ///         The template's validation node allows ONE attempt and routes no retry, so a scripted failure lands it in
    ///         front of a human on the first answer — which is the state an operator answers <c>Skip</c> on.
    ///     </para>
    /// </summary>
    public const string DecompositionWithVerification = """
                                                        {
                                                          "schemaVersion": 1,
                                                          "nodes": [
                                                            { "nodeKey": "plan", "nodeType": "Agent", "label": "Plan",
                                                              "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                            { "nodeKey": "planapproval", "nodeType": "HumanGate", "label": "Approve the plan" },
                                                            { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose",
                                                              "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b",
                                                              "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                                            { "nodeKey": "implement", "nodeType": "Agent", "label": "Implement",
                                                              "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                            { "nodeKey": "validate", "nodeType": "Tool", "label": "Validate", "maxAttempts": 1 },
                                                            { "nodeKey": "join", "nodeType": "Join", "label": "Every slice implemented" },
                                                            { "nodeKey": "verify", "nodeType": "Agent", "label": "Verify",
                                                              "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" }
                                                          ],
                                                          "edges": [
                                                            { "from": "plan", "to": "planapproval" },
                                                            { "from": "planapproval", "to": "decompose", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                            { "from": "planapproval", "to": "verify", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                            { "from": "decompose", "to": "join" },
                                                            { "from": "implement", "to": "validate" },
                                                            { "from": "validate", "to": "join" },
                                                            { "from": "join", "to": "verify" }
                                                          ]
                                                        }
                                                        """;

    /// <summary>
    ///     <see cref="DecompositionSubtree" /> with the implementation node the seeded template will really carry: a
    ///     <c>DevTask</c>, so each clone drives a Development task of its OWN and the isolation those task ids buy is
    ///     observable rather than argued. <c>nodeTimeoutSeconds</c> is mandatory on a DevTask node per the C template
    ///     rule.
    /// </summary>
    public const string DecompositionIntoDevTasks = """
                                                    {
                                                      "schemaVersion": 1,
                                                      "nodes": [
                                                        { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose",
                                                          "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b",
                                                          "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                                        { "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "nodeTimeoutSeconds": 900 },
                                                        { "nodeKey": "validate", "nodeType": "Tool", "retryTarget": "implement" },
                                                        { "nodeKey": "join", "nodeType": "Join" }
                                                      ],
                                                      "edges": [
                                                        { "from": "decompose", "to": "join" },
                                                        { "from": "implement", "to": "validate" },
                                                        { "from": "validate", "to": "join" }
                                                      ]
                                                    }
                                                    """;

    /// <summary>
    ///     A template whose ROOT is an Agent and which carries a <c>DevTask</c> below it: the brief is written by a
    ///     session, the code is written by a coder. A custom shape rather than the seeded one, and the reason the
    ///     "must name its files" rule is asked of the whole subtree — the coder that cannot finish on an empty patch is
    ///     here too, one node further down, where reading only the root would miss it.
    /// </summary>
    public const string DecompositionIntoAnAgentOverADevTask = """
                                                               {
                                                                 "schemaVersion": 1,
                                                                 "nodes": [
                                                                   { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose",
                                                                     "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b",
                                                                     "materialization": { "templateNodeKey": "prepare", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                                                   { "nodeKey": "prepare", "nodeType": "Agent", "label": "Prepare",
                                                                     "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b" },
                                                                   { "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "nodeTimeoutSeconds": 900 },
                                                                   { "nodeKey": "join", "nodeType": "Join" }
                                                                 ],
                                                                 "edges": [
                                                                   { "from": "decompose", "to": "join" },
                                                                   { "from": "prepare", "to": "implement" },
                                                                   { "from": "implement", "to": "join" }
                                                                 ]
                                                               }
                                                               """;

    /// <summary>
    ///     <see cref="DecompositionIntoDevTasks" /> with the integration stage on the end: the fan-out joins, an
    ///     operator is asked, and only their approval routes into the node that applies the patches. The seeded
    ///     <c>feature-development-v1</c> shape without its research, plan and verification agents, which add ticks and
    ///     sessions to script and nothing to what integration does.
    /// </summary>
    public const string DecompositionIntoDevTasksAndIntegration = """
                                                                  {
                                                                    "schemaVersion": 1,
                                                                    "nodes": [
                                                                      { "nodeKey": "decompose", "nodeType": "Agent", "label": "Decompose",
                                                                        "agentDefinitionId": "6f5b1f3a-1c2d-4f5e-8a9b-0c1d2e3f4a5b",
                                                                        "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                                                      { "nodeKey": "implement", "nodeType": "DevTask", "label": "Implement", "nodeTimeoutSeconds": 900 },
                                                                      { "nodeKey": "validate", "nodeType": "Tool", "retryTarget": "implement" },
                                                                      { "nodeKey": "join", "nodeType": "Join" },
                                                                      { "nodeKey": "integrationapproval", "nodeType": "HumanGate", "label": "Approve integration" },
                                                                      { "nodeKey": "integrate", "nodeType": "Tool", "toolMode": "Apply", "label": "Apply the approved patches" },
                                                                      { "nodeKey": "fullvalidate", "nodeType": "Tool", "label": "Validate the integrated result" }
                                                                    ],
                                                                    "edges": [
                                                                      { "from": "decompose", "to": "join" },
                                                                      { "from": "implement", "to": "validate" },
                                                                      { "from": "validate", "to": "join" },
                                                                      { "from": "join", "to": "integrationapproval" },
                                                                      { "from": "integrationapproval", "to": "integrate", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                                                      { "from": "integrate", "to": "fullvalidate" }
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
    ///     A fan-out WIDER than the sandbox lane, so the cap is observed under real contention rather than inferred
    ///     from a lane of one. Four tool nodes admitted by the same tick against two slots.
    /// </summary>
    public const string FourParallelTools = """
                                            {
                                              "schemaVersion": 1,
                                              "nodes": [
                                                { "nodeKey": "fanout", "nodeType": "Parallel" },
                                                { "nodeKey": "lanea", "nodeType": "Tool" },
                                                { "nodeKey": "laneb", "nodeType": "Tool" },
                                                { "nodeKey": "lanec", "nodeType": "Tool" },
                                                { "nodeKey": "laned", "nodeType": "Tool" },
                                                { "nodeKey": "lanejoin", "nodeType": "Join" }
                                              ],
                                              "edges": [
                                                { "from": "fanout", "to": "lanea" },
                                                { "from": "fanout", "to": "laneb" },
                                                { "from": "fanout", "to": "lanec" },
                                                { "from": "fanout", "to": "laned" },
                                                { "from": "lanea", "to": "lanejoin" },
                                                { "from": "laneb", "to": "lanejoin" },
                                                { "from": "lanec", "to": "lanejoin" },
                                                { "from": "laned", "to": "lanejoin" }
                                              ]
                                            }
                                            """;

    /// <summary>
    ///     A WORK node that waits on <c>Any</c> — two branches in, one enough to admit it — with an <c>All</c> join
    ///     behind it that also has a branch of its own. The shape that tells a skip's two origins apart under a policy
    ///     that is not <c>All</c>: the node runs on one satisfied edge while its sibling is already dead, and what an
    ///     operator's Skip on it then means cannot be read off that dead sibling.
    /// </summary>
    public const string AnyWorkNodeOverAMixedFanIn = """
                                                     {
                                                       "schemaVersion": 1,
                                                       "nodes": [
                                                         { "nodeKey": "mixedsplit", "nodeType": "Parallel" },
                                                         { "nodeKey": "mixedgood", "nodeType": "Tool" },
                                                         { "nodeKey": "mixedbad", "nodeType": "Tool" },
                                                         { "nodeKey": "mixedwork", "nodeType": "Tool", "joinPolicy": "Any" },
                                                         { "nodeKey": "mixedsibling", "nodeType": "Tool" },
                                                         { "nodeKey": "mixedmerge", "nodeType": "Join" }
                                                       ],
                                                       "edges": [
                                                         { "from": "mixedsplit", "to": "mixedgood" },
                                                         { "from": "mixedsplit", "to": "mixedbad" },
                                                         { "from": "mixedsplit", "to": "mixedsibling" },
                                                         { "from": "mixedgood", "to": "mixedwork" },
                                                         { "from": "mixedbad", "to": "mixedwork" },
                                                         { "from": "mixedwork", "to": "mixedmerge" },
                                                         { "from": "mixedsibling", "to": "mixedmerge" }
                                                       ]
                                                     }
                                                     """;

    /// <summary>
    ///     Two branches into an <c>Any</c> join, one of which cannot run at all: the agent node binds no definition, so
    ///     it stands down for a human and the operator's answer decides which terminal the dead branch carries.
    /// </summary>
    public const string AnyJoinOverADeadBranch = """
                                                 {
                                                   "schemaVersion": 1,
                                                   "nodes": [
                                                     { "nodeKey": "anysplit", "nodeType": "Parallel" },
                                                     { "nodeKey": "anysurvivor", "nodeType": "Tool" },
                                                     { "nodeKey": "anydoomed", "nodeType": "Agent" },
                                                     { "nodeKey": "anymerge", "nodeType": "Join", "joinPolicy": "Any" }
                                                   ],
                                                   "edges": [
                                                     { "from": "anysplit", "to": "anysurvivor" },
                                                     { "from": "anysplit", "to": "anydoomed" },
                                                     { "from": "anysurvivor", "to": "anymerge" },
                                                     { "from": "anydoomed", "to": "anymerge" }
                                                   ]
                                                 }
                                                 """;

    /// <summary>
    ///     Two branches into an <c>All</c> join with a tail behind it, one branch unable to run at all: the agent node
    ///     binds no definition, so it stands down for a human and an operator's Skip is what decides the join. The
    ///     shape the live C1 finding took — one leaf excused, its siblings' work still worth carrying.
    /// </summary>
    public const string AllJoinOverASkippedBranch = """
                                                    {
                                                      "schemaVersion": 1,
                                                      "nodes": [
                                                        { "nodeKey": "allsplit", "nodeType": "Parallel" },
                                                        { "nodeKey": "allsurvivor", "nodeType": "Tool" },
                                                        { "nodeKey": "alldoomed", "nodeType": "Agent" },
                                                        { "nodeKey": "allmerge", "nodeType": "Join" },
                                                        { "nodeKey": "alltail", "nodeType": "Tool" }
                                                      ],
                                                      "edges": [
                                                        { "from": "allsplit", "to": "allsurvivor" },
                                                        { "from": "allsplit", "to": "alldoomed" },
                                                        { "from": "allsurvivor", "to": "allmerge" },
                                                        { "from": "alldoomed", "to": "allmerge" },
                                                        { "from": "allmerge", "to": "alltail" }
                                                      ]
                                                    }
                                                    """;

    /// <summary>
    ///     A skipped node with a FAILED ancestor behind it, beside a branch that succeeded. The distinction the waiver
    ///     rule turns on: this skip is a cascade off real breakage, not an operator excusing a slice.
    /// </summary>
    public const string FanOutOverAFailingChain = """
                                                  {
                                                    "schemaVersion": 1,
                                                    "nodes": [
                                                      { "nodeKey": "start", "nodeType": "Parallel" },
                                                      { "nodeKey": "lint", "nodeType": "Tool" },
                                                      { "nodeKey": "broken", "nodeType": "Tool" },
                                                      { "nodeKey": "after", "nodeType": "Tool" },
                                                      { "nodeKey": "join", "nodeType": "Join" }
                                                    ],
                                                    "edges": [
                                                      { "from": "start", "to": "lint" },
                                                      { "from": "start", "to": "broken" },
                                                      { "from": "broken", "to": "after" },
                                                      { "from": "lint", "to": "join" },
                                                      { "from": "after", "to": "join" }
                                                    ]
                                                  }
                                                  """;

    /// <summary>
    ///     A gate whose two branches both reach the same <c>All</c> join, plus a conditional edge from the gate
    ///     straight into it. The branch the condition did not take is Skipped like any other, and it must NOT be
    ///     excused: nothing chose it, the graph refused it. The gate's own edge is listed FIRST on purpose — it is the
    ///     dead edge a run took no notice of, sitting in front of the one that is actually news.
    /// </summary>
    public const string GateBranchesIntoAJoin = """
                                                {
                                                  "schemaVersion": 1,
                                                  "nodes": [
                                                    { "nodeKey": "gate", "nodeType": "Gate" },
                                                    { "nodeKey": "taken", "nodeType": "Tool" },
                                                    { "nodeKey": "nottaken", "nodeType": "Tool" },
                                                    { "nodeKey": "join", "nodeType": "Join" }
                                                  ],
                                                  "edges": [
                                                    { "from": "gate", "to": "taken", "condition": { "path": "passed", "op": "eq", "value": true } },
                                                    { "from": "gate", "to": "nottaken", "condition": { "path": "passed", "op": "eq", "value": false } },
                                                    { "from": "gate", "to": "join", "condition": { "path": "passed", "op": "eq", "value": false } },
                                                    { "from": "taken", "to": "join" },
                                                    { "from": "nottaken", "to": "join" }
                                                  ]
                                                }
                                                """;

    /// <summary>
    ///     A decomposition AS MATERIALIZED: the clone leaves wired to the join, and the decomposition's own edge into
    ///     it kept beside them. The shape that decides what happens when every clone is skipped.
    /// </summary>
    public const string MaterializedDecompositionJoin = """
                                                        {
                                                          "schemaVersion": 1,
                                                          "nodes": [
                                                            { "nodeKey": "decompose", "nodeType": "Agent" },
                                                            { "nodeKey": "implement#one", "nodeType": "DevTask" },
                                                            { "nodeKey": "implement#two", "nodeType": "DevTask" },
                                                            { "nodeKey": "join", "nodeType": "Join" }
                                                          ],
                                                          "edges": [
                                                            { "from": "decompose", "to": "implement#one" },
                                                            { "from": "decompose", "to": "implement#two" },
                                                            { "from": "decompose", "to": "join" },
                                                            { "from": "implement#one", "to": "join" },
                                                            { "from": "implement#two", "to": "join" }
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
