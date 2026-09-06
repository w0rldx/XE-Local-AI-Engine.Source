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

    /// <summary>
    ///     An <c>End</c> that projects the run's result out of its input document instead of carrying the whole thing.
    ///     The path reaches through <c>input</c> — the single satisfied predecessor's output document — which is the
    ///     shape every End node with one inbound edge sees.
    /// </summary>
    public const string EndWithResultPath = """
                                            {
                                              "schemaVersion": 1,
                                              "nodes": [
                                                { "key": "start", "kind": "Start" },
                                                { "key": "analyze", "kind": "Agent", "config": { "instructions": "Judge it." } },
                                                { "key": "done", "kind": "End", "config": { "outcome": "completed", "resultPath": "input.output.json" } }
                                              ],
                                              "edges": [
                                                { "key": "e1", "from": "start", "to": "analyze" },
                                                { "key": "e2", "from": "analyze", "to": "done" }
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

    /// <summary>
    ///     A linear run of nothing but inline kinds, so a whole run walks to <c>Completed</c> in a build with no lane.
    ///     One layer per tick is the property it exists to make visible.
    /// </summary>
    public const string InlineLinear = """
                                       {
                                         "schemaVersion": 1,
                                         "nodes": [
                                           { "key": "start", "kind": "Start" },
                                           { "key": "middle", "kind": "Parallel", "config": {} },
                                           { "key": "done", "kind": "End", "config": { "outcome": "completed", "resultPath": "input.output" } }
                                         ],
                                         "edges": [
                                           { "key": "e1", "from": "start", "to": "middle" },
                                           { "key": "e2", "from": "middle", "to": "done" }
                                         ]
                                       }
                                       """;

    /// <summary>
    ///     A <c>Condition</c> routing on the RUN INPUT carried through <c>Start</c>'s output document, with the branch
    ///     not taken cascading a skip one node further. Inline throughout, so the routing is observable in a build whose
    ///     only executor is the inline one.
    /// </summary>
    public const string InlineBranch = """
                                       {
                                         "schemaVersion": 1,
                                         "nodes": [
                                           { "key": "start", "kind": "Start" },
                                           { "key": "check", "kind": "Condition", "config": { "path": "output.input.requiresReview" } },
                                           { "key": "yes", "kind": "Parallel", "config": {} },
                                           { "key": "no", "kind": "Parallel", "config": {} },
                                           { "key": "after", "kind": "Parallel", "config": {} },
                                           { "key": "done", "kind": "End", "joinPolicy": "Any", "config": { "outcome": "completed" } }
                                         ],
                                         "edges": [
                                           { "key": "e1", "from": "start", "to": "check" },
                                           { "key": "e2", "from": "check", "to": "yes", "label": "yes", "condition": { "op": "eq", "value": true } },
                                           { "key": "e3", "from": "check", "to": "no", "label": "no", "condition": { "op": "ne", "value": true } },
                                           { "key": "e4", "from": "yes", "to": "done" },
                                           { "key": "e5", "from": "no", "to": "after" },
                                           { "key": "e6", "from": "after", "to": "done" }
                                         ]
                                       }
                                       """;

    /// <summary>
    ///     An inline fan-out whose two branches are of DIFFERENT lengths, which is what makes an <c>All</c> join's wait
    ///     observable: the short branch lands a tick before the long one, and the join must not proceed on it.
    /// </summary>
    public const string InlineJoinAll = """
                                        {
                                          "schemaVersion": 1,
                                          "nodes": [
                                            { "key": "start", "kind": "Start" },
                                            { "key": "fanout", "kind": "Parallel", "config": {} },
                                            { "key": "fast", "kind": "Parallel", "config": {} },
                                            { "key": "slow", "kind": "Parallel", "config": {} },
                                            { "key": "slower", "kind": "Parallel", "config": {} },
                                            { "key": "merge", "kind": "Join", "config": {} },
                                            { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                          ],
                                          "edges": [
                                            { "key": "e1", "from": "start", "to": "fanout" },
                                            { "key": "e2", "from": "fanout", "to": "fast" },
                                            { "key": "e3", "from": "fanout", "to": "slow" },
                                            { "key": "e4", "from": "slow", "to": "slower" },
                                            { "key": "e5", "from": "fast", "to": "merge" },
                                            { "key": "e6", "from": "slower", "to": "merge" },
                                            { "key": "e7", "from": "merge", "to": "done" }
                                          ]
                                        }
                                        """;

    /// <summary>
    ///     The same shape merged under <c>Any</c>, where one branch arriving is the whole contract — and where a run
    ///     input that kills BOTH branches leaves the join with nothing that can ever arrive.
    /// </summary>
    public const string InlineJoinAny = """
                                        {
                                          "schemaVersion": 1,
                                          "nodes": [
                                            { "key": "start", "kind": "Start" },
                                            { "key": "check", "kind": "Condition", "config": { "path": "output.input.route" } },
                                            { "key": "left", "kind": "Parallel", "config": {} },
                                            { "key": "right", "kind": "Parallel", "config": {} },
                                            { "key": "merge", "kind": "Join", "joinPolicy": "Any", "config": {} },
                                            { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                          ],
                                          "edges": [
                                            { "key": "e1", "from": "start", "to": "check" },
                                            { "key": "e2", "from": "check", "to": "left", "label": "left", "condition": { "op": "eq", "value": "left" } },
                                            { "key": "e3", "from": "check", "to": "right", "label": "right", "condition": { "op": "eq", "value": "right" } },
                                            { "key": "e4", "from": "left", "to": "merge" },
                                            { "key": "e5", "from": "right", "to": "merge" },
                                            { "key": "e6", "from": "merge", "to": "done" }
                                          ]
                                        }
                                        """;

    /// <summary>
    ///     A single inline work node declaring three attempts, so a retry has budget to spend and the run-wide cap has
    ///     something to refuse.
    /// </summary>
    public const string InlineRetryable = """
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              { "key": "start", "kind": "Start" },
                                              { "key": "work", "kind": "Parallel", "maxAttempts": 3, "config": {} },
                                              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                            ],
                                            "edges": [
                                              { "key": "e1", "from": "start", "to": "work" },
                                              { "key": "e2", "from": "work", "to": "done" }
                                            ]
                                          }
                                          """;

    /// <summary>The same shape with the shipped single-attempt default, so a failure has nowhere to go.</summary>
    public const string InlineSingleAttempt = """
                                              {
                                                "schemaVersion": 1,
                                                "nodes": [
                                                  { "key": "start", "kind": "Start" },
                                                  { "key": "work", "kind": "Parallel", "config": {} },
                                                  { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                                ],
                                                "edges": [
                                                  { "key": "e1", "from": "start", "to": "work" },
                                                  { "key": "e2", "from": "work", "to": "done" }
                                                ]
                                              }
                                              """;

    /// <summary>
    ///     An <c>Agent</c> node in an otherwise inline graph: the smallest shape that puts one turn through the agent
    ///     lane and nothing else through anything.
    /// </summary>
    public const string InlineWithAgent = """
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              { "key": "start", "kind": "Start" },
                                              { "key": "analyze", "kind": "Agent", "config": { "instructions": "Judge it." } },
                                              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                            ],
                                            "edges": [
                                              { "key": "e1", "from": "start", "to": "analyze" },
                                              { "key": "e2", "from": "analyze", "to": "done" }
                                            ]
                                          }
                                          """;

    /// <summary>
    ///     The same shape with the <c>Agent</c> held to ONE attempt, against the three a work node gets by default, so
    ///     a failure it carries has nowhere to go and the retry stage has to refuse it.
    /// </summary>
    public const string InlineWithSingleAttemptAgent = """
                                                       {
                                                         "schemaVersion": 1,
                                                         "nodes": [
                                                           { "key": "start", "kind": "Start" },
                                                           { "key": "analyze", "kind": "Agent", "maxAttempts": 1, "config": { "instructions": "Judge it." } },
                                                           { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                                         ],
                                                         "edges": [
                                                           { "key": "e1", "from": "start", "to": "analyze" },
                                                           { "key": "e2", "from": "analyze", "to": "done" }
                                                         ]
                                                       }
                                                       """;

    ///     The live-validation shape: an <c>Agent</c> node under a response schema, a <c>Condition</c> routing on the
    ///     answer it parsed, two mutually exclusive branches, a join and an <c>End</c>.
    ///     <para>
    ///         The join is <c>Any</c> and has to be. Its two inbound branches are the two arms of one Condition, so
    ///         exactly one of them is always dead — and an <c>All</c> join over a dead edge is SKIPPED, which would
    ///         skip the End behind it and leave the run <c>Cancelled</c> rather than <c>Completed</c>. The S1 plan's
    ///         live script says "Join, All"; the shipped admission rule says that graph cannot complete.
    ///     </para>
    /// </summary>
    public const string AgentBranchJoin = """
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              { "key": "start", "kind": "Start" },
                                              { "key": "analyze", "kind": "Agent",
                                                "config": { "instructions": "Judge whether this needs review.",
                                                            "responseJsonSchema": { "type": "object",
                                                                                    "properties": { "requiresReview": { "type": "boolean" }, "summary": { "type": "string" } },
                                                                                    "required": ["requiresReview", "summary"] } } },
                                              { "key": "check", "kind": "Condition", "config": { "path": "output.json.requiresReview" } },
                                              { "key": "review", "kind": "Parallel", "config": {} },
                                              { "key": "quick", "kind": "Parallel", "config": {} },
                                              { "key": "merge", "kind": "Join", "joinPolicy": "Any", "config": {} },
                                              { "key": "done", "kind": "End", "config": { "outcome": "completed", "resultPath": "input.output" } }
                                            ],
                                            "edges": [
                                              { "key": "e1", "from": "start", "to": "analyze" },
                                              { "key": "e2", "from": "analyze", "to": "check" },
                                              { "key": "e3", "from": "check", "to": "review", "label": "yes", "condition": { "op": "eq", "value": true } },
                                              { "key": "e4", "from": "check", "to": "quick", "label": "no", "condition": { "op": "ne", "value": true } },
                                              { "key": "e5", "from": "review", "to": "merge" },
                                              { "key": "e6", "from": "quick", "to": "merge" },
                                              { "key": "e7", "from": "merge", "to": "done" }
                                            ]
                                          }
                                          """;

    /// <summary>
    ///     Three <c>Agent</c> nodes fanned out in parallel. The node has ONE invocation slot whatever the lane's own
    ///     width, so this is the shape that makes the queue honest: one row runs and two say what they are waiting for.
    /// </summary>
    public const string AgentFanOut = """
                                      {
                                        "schemaVersion": 1,
                                        "nodes": [
                                          { "key": "start", "kind": "Start" },
                                          { "key": "fanout", "kind": "Parallel", "config": {} },
                                          { "key": "left", "kind": "Agent", "config": { "instructions": "Left." } },
                                          { "key": "middle", "kind": "Agent", "config": { "instructions": "Middle." } },
                                          { "key": "right", "kind": "Agent", "config": { "instructions": "Right." } },
                                          { "key": "merge", "kind": "Join", "config": {} },
                                          { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                        ],
                                        "edges": [
                                          { "key": "e1", "from": "start", "to": "fanout" },
                                          { "key": "e2", "from": "fanout", "to": "left" },
                                          { "key": "e3", "from": "fanout", "to": "middle" },
                                          { "key": "e4", "from": "fanout", "to": "right" },
                                          { "key": "e5", "from": "left", "to": "merge" },
                                          { "key": "e6", "from": "middle", "to": "merge" },
                                          { "key": "e7", "from": "right", "to": "merge" },
                                          { "key": "e8", "from": "merge", "to": "done" }
                                        ]
                                      }
                                      """;

    /// <summary>
    ///     Structurally sound and refused by the D6 tool gate alone: <c>run_python</c> parses as a tool name like any
    ///     other, and only the catalog knows it is WriteExecute. One offending node, so the error keying is readable.
    /// </summary>
    public const string ToolValidationWriteExecuteTool = """
                                                         {
                                                           "schemaVersion": 1,
                                                           "nodes": [
                                                             { "key": "start", "kind": "Start" },
                                                             { "key": "runner", "kind": "Tool", "config": { "toolName": "run_python" } },
                                                             { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                                           ],
                                                           "edges": [
                                                             { "key": "e1", "from": "start", "to": "runner" },
                                                             { "key": "e2", "from": "runner", "to": "done" }
                                                           ]
                                                         }
                                                         """;

    /// <summary>
    ///     Two Tool nodes outside the envelope for two different reasons — a write tool and an approval-gated one — so
    ///     the gate has to report BOTH keys rather than stopping at the first.
    /// </summary>
    public const string ToolValidationTwoRefusedTools = """
                                                        {
                                                          "schemaVersion": 1,
                                                          "nodes": [
                                                            { "key": "start", "kind": "Start" },
                                                            { "key": "runner", "kind": "Tool", "config": { "toolName": "run_python" } },
                                                            { "key": "asker", "kind": "Tool", "config": { "toolName": "ask_user" } },
                                                            { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                                          ],
                                                          "edges": [
                                                            { "key": "e1", "from": "start", "to": "runner" },
                                                            { "key": "e2", "from": "runner", "to": "asker" },
                                                            { "key": "e3", "from": "asker", "to": "done" }
                                                          ]
                                                        }
                                                        """;
}
