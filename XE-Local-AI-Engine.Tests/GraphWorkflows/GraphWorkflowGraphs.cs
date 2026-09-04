namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

/// <summary>
///     The graph fixtures the graph workflow suites route over, named for the SHAPE rather than for the test that uses
///     them. Later slices add their run shapes here rather than copying a graph into a suite of their own.
/// </summary>
internal static class GraphWorkflowGraphs
{
    /// <summary>The smallest legal definition: one Start, one Agent, one End.</summary>
    public const string StartAgentEnd = """
                                        {
                                          "schemaVersion": 1,
                                          "nodes": [
                                            { "key": "start", "kind": "Start", "label": "Start", "config": {} },
                                            { "key": "analyze", "kind": "Agent", "label": "Analyze", "config": { "instructions": "Analyze the input." } },
                                            { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                          ],
                                          "edges": [
                                            { "key": "e1", "from": "start", "to": "analyze" },
                                            { "key": "e2", "from": "analyze", "to": "done" }
                                          ]
                                        }
                                        """;

    /// <summary>
    ///     A Condition node routing on a JSON answer, its two branches reconverging on an <c>Any</c> End. The edges
    ///     carry a label and a sourceHandle, which is the authoring metadata the runtime reads past.
    /// </summary>
    public const string BranchOnJson = """
                                       {
                                         "schemaVersion": 1,
                                         "nodes": [
                                           { "key": "start", "kind": "Start" },
                                           { "key": "analyze", "kind": "Agent",
                                             "config": { "instructions": "Judge it.",
                                                         "responseJsonSchema": { "type": "object", "properties": { "requiresReview": { "type": "boolean" } } } } },
                                           { "key": "check", "kind": "Condition", "config": { "path": "output.json.requiresReview" } },
                                           { "key": "review", "kind": "Agent", "config": { "instructions": "Review it." } },
                                           { "key": "ship", "kind": "Agent", "config": { "instructions": "Ship it." } },
                                           { "key": "done", "kind": "End", "joinPolicy": "Any", "config": { "outcome": "completed" } }
                                         ],
                                         "edges": [
                                           { "key": "e1", "from": "start", "to": "analyze" },
                                           { "key": "e2", "from": "analyze", "to": "check" },
                                           { "key": "e3", "from": "check", "to": "review", "label": "yes", "sourceHandle": "true", "condition": { "op": "eq", "value": true } },
                                           { "key": "e4", "from": "check", "to": "ship", "label": "no", "sourceHandle": "false", "condition": { "op": "ne", "value": true } },
                                           { "key": "e5", "from": "review", "to": "done" },
                                           { "key": "e6", "from": "ship", "to": "done" }
                                         ]
                                       }
                                       """;

    /// <summary>
    ///     A Condition node with one conditional branch and one unconditional default, plus a conditional edge from the
    ///     Condition straight into the join. That last edge is listed FIRST among the join's inbound edges on purpose:
    ///     it is the dead edge a run took no notice of, sitting in front of the one that is actually news.
    /// </summary>
    public const string ConditionWithDefault = """
                                               {
                                                 "schemaVersion": 1,
                                                 "nodes": [
                                                   { "key": "start", "kind": "Start" },
                                                   { "key": "check", "kind": "Condition", "config": { "path": "output.json.ok" } },
                                                   { "key": "yes", "kind": "Agent", "config": { "instructions": "Carry on." } },
                                                   { "key": "fallback", "kind": "Agent", "config": { "instructions": "Always run." } },
                                                   { "key": "merge", "kind": "Join", "config": {} },
                                                   { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                                 ],
                                                 "edges": [
                                                   { "key": "e1", "from": "start", "to": "check" },
                                                   { "key": "e2", "from": "check", "to": "merge", "condition": { "op": "eq", "value": true } },
                                                   { "key": "e3", "from": "check", "to": "yes", "condition": { "op": "eq", "value": true } },
                                                   { "key": "e4", "from": "check", "to": "fallback" },
                                                   { "key": "e5", "from": "yes", "to": "merge" },
                                                   { "key": "e6", "from": "fallback", "to": "merge" },
                                                   { "key": "e7", "from": "merge", "to": "done" }
                                                 ]
                                               }
                                               """;

    /// <summary>A pause offering both decisions, each with an out-edge that fires for it — the pre-flight rule satisfied.</summary>
    public const string PauseTwoDecisions = """
                                            {
                                              "schemaVersion": 1,
                                              "nodes": [
                                                { "key": "start", "kind": "Start" },
                                                { "key": "review", "kind": "Pause",
                                                  "config": { "prompt": "Approve the analysis?", "allowedDecisions": ["Approve", "Reject"], "requireComment": false } },
                                                { "key": "shipped", "kind": "End", "config": { "outcome": "completed" } },
                                                { "key": "rejected", "kind": "End", "config": { "outcome": "rejected" } }
                                              ],
                                              "edges": [
                                                { "key": "e1", "from": "start", "to": "review" },
                                                { "key": "e2", "from": "review", "to": "shipped", "label": "approved",
                                                  "condition": { "path": "output.decision", "op": "eq", "value": "Approve" } },
                                                { "key": "e3", "from": "review", "to": "rejected", "label": "rejected",
                                                  "condition": { "path": "output.decision", "op": "eq", "value": "Reject" } }
                                              ]
                                            }
                                            """;

    /// <summary>
    ///     A fan-out into a <c>Join</c> node AND into an ordinary Agent node that also has two inbound edges. The
    ///     second is the one that matters: a join policy is a property of every node, and reading it off <c>Join</c>
    ///     alone is the documented trap.
    /// </summary>
    public const string ParallelJoinAll = """
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              { "key": "start", "kind": "Start" },
                                              { "key": "fanout", "kind": "Parallel", "config": {} },
                                              { "key": "left", "kind": "Agent", "config": { "instructions": "Left." } },
                                              { "key": "right", "kind": "Agent", "config": { "instructions": "Right." } },
                                              { "key": "merge", "kind": "Join", "config": {} },
                                              { "key": "summary", "kind": "Agent", "config": { "instructions": "Summarize both." } },
                                              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                            ],
                                            "edges": [
                                              { "key": "e1", "from": "start", "to": "fanout" },
                                              { "key": "e2", "from": "fanout", "to": "left" },
                                              { "key": "e3", "from": "fanout", "to": "right" },
                                              { "key": "e4", "from": "left", "to": "merge" },
                                              { "key": "e5", "from": "right", "to": "merge" },
                                              { "key": "e6", "from": "left", "to": "summary" },
                                              { "key": "e7", "from": "right", "to": "summary" },
                                              { "key": "e8", "from": "merge", "to": "done" },
                                              { "key": "e9", "from": "summary", "to": "done" }
                                            ]
                                          }
                                          """;

    /// <summary>The same fan-out merged under <c>Any</c>: one branch arriving is the whole contract.</summary>
    public const string ParallelJoinAny = """
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              { "key": "start", "kind": "Start" },
                                              { "key": "fanout", "kind": "Parallel", "config": {} },
                                              { "key": "left", "kind": "Agent", "config": { "instructions": "Left." } },
                                              { "key": "right", "kind": "Agent", "config": { "instructions": "Right." } },
                                              { "key": "merge", "kind": "Join", "joinPolicy": "Any", "config": {} },
                                              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                            ],
                                            "edges": [
                                              { "key": "e1", "from": "start", "to": "fanout" },
                                              { "key": "e2", "from": "fanout", "to": "left" },
                                              { "key": "e3", "from": "fanout", "to": "right" },
                                              { "key": "e4", "from": "left", "to": "merge" },
                                              { "key": "e5", "from": "right", "to": "merge" },
                                              { "key": "e6", "from": "merge", "to": "done" }
                                            ]
                                          }
                                          """;

    /// <summary>Two Tool nodes in a row, so the tool-name seam has more than one name to list.</summary>
    public const string ToolNode = """
                                   {
                                     "schemaVersion": 1,
                                     "nodes": [
                                       { "key": "start", "kind": "Start" },
                                       { "key": "lookup", "kind": "Tool", "position": { "x": 12, "y": -4 },
                                         "config": { "toolName": "read_file", "arguments": { "path": "notes.md" },
                                                     "argumentBindings": { "path": "output.json.path" } } },
                                       { "key": "peek", "kind": "Tool", "config": { "toolName": "list_files" } },
                                       { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                     ],
                                     "edges": [
                                       { "key": "e1", "from": "start", "to": "lookup" },
                                       { "key": "e2", "from": "lookup", "to": "peek" },
                                       { "key": "e3", "from": "peek", "to": "done" }
                                     ]
                                   }
                                   """;

    /// <summary>
    ///     Structurally sound — one Start, one End, acyclic, everything reachable — and wrong in two per-node ways at
    ///     once: an Agent naming a reasoning effort nothing offers, and an Agent whose config carries a Tool node's
    ///     member. Both accumulate against their own node key, which is what a multi-error refusal has to prove.
    /// </summary>
    public const string TwoNodeConfigErrors = """
                                              {
                                                "schemaVersion": 1,
                                                "nodes": [
                                                  { "key": "start", "kind": "Start", "config": {} },
                                                  { "key": "a", "kind": "Agent", "config": { "instructions": "Judge it.", "reasoningEffort": "extreme" } },
                                                  { "key": "b", "kind": "Agent", "config": { "instructions": "Ship it.", "toolName": "read_file" } },
                                                  { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                                ],
                                                "edges": [
                                                  { "key": "e1", "from": "start", "to": "a" },
                                                  { "key": "e2", "from": "a", "to": "b" },
                                                  { "key": "e3", "from": "b", "to": "done" }
                                                ]
                                              }
                                              """;

    /// <summary>
    ///     One branch comparing against an EXPLICIT JSON null, beside one whose operator takes no value at all. The
    ///     two absences are different — a value that is null, and no value member — and a round trip that collapses
    ///     them turns the first into the second, which the parser refuses.
    /// </summary>
    public const string ConditionOnExplicitNull = """
                                                  {
                                                    "schemaVersion": 1,
                                                    "nodes": [
                                                      { "key": "start", "kind": "Start" },
                                                      { "key": "check", "kind": "Condition", "config": { "path": "output.json.reason" } },
                                                      { "key": "unset", "kind": "End", "config": { "outcome": "completed" } },
                                                      { "key": "given", "kind": "End", "config": { "outcome": "completed" } }
                                                    ],
                                                    "edges": [
                                                      { "key": "e1", "from": "start", "to": "check" },
                                                      { "key": "e2", "from": "check", "to": "unset", "condition": { "op": "eq", "value": null } },
                                                      { "key": "e3", "from": "check", "to": "given", "condition": { "op": "exists" } }
                                                    ]
                                                  }
                                                  """;

    /// <summary>Two End nodes, so "the run reached an end" is not the same question as "the last node succeeded".</summary>
    public const string TwoEnds = """
                                  {
                                    "schemaVersion": 1,
                                    "nodes": [
                                      { "key": "start", "kind": "Start" },
                                      { "key": "check", "kind": "Condition", "config": { "path": "output.json.ok" } },
                                      { "key": "okend", "kind": "End", "config": { "outcome": "completed" } },
                                      { "key": "badend", "kind": "End", "config": { "outcome": "rejected" } }
                                    ],
                                    "edges": [
                                      { "key": "e1", "from": "start", "to": "check" },
                                      { "key": "e2", "from": "check", "to": "okend", "condition": { "op": "eq", "value": true } },
                                      { "key": "e3", "from": "check", "to": "badend", "condition": { "op": "ne", "value": true } }
                                    ]
                                  }
                                  """;
}
