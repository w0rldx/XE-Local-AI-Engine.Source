namespace XE_Local_AI_Engine.Tests.GraphWorkflows.Import;

/// <summary>
///     Stored Open Canvas graph blobs, written out by hand rather than built through the Open Canvas model. That
///     namespace was deleted in the phase after the one these fixtures were added for, so a builder reference here
///     would have taken this suite with it; a JSON literal is what the database actually holds and it outlives the
///     type that wrote it.
///     <para>
///         Web-defaults camelCase members, <c>kind</c> as the enum's own name, and null agent fields simply absent —
///         which is exactly what the Open Canvas graph serializer produced.
///     </para>
/// </summary>
internal static class CanvasGraphs
{
    /// <summary>The shape almost every stored row has: the strictly linear chain the canvas validator enforced.</summary>
    public const string Linear = """
                                 {
                                   "startText": "Summarize the release notes.",
                                   "nodes": [
                                     { "id": "start", "kind": "Start" },
                                     { "id": "agent-1", "kind": "Agent", "label": "Summarize", "instructions": "Summarize it.", "model": "qwen3:8b" },
                                     { "id": "end", "kind": "End" }
                                   ],
                                   "edges": [
                                     { "sourceId": "start", "targetId": "agent-1" },
                                     { "sourceId": "agent-1", "targetId": "end" }
                                   ]
                                 }
                                 """;

    /// <summary>A Debug tap between two agents — the node that has no destination here and is elided.</summary>
    public const string WithDebug = """
                                    {
                                      "startText": "Draft it.",
                                      "nodes": [
                                        { "id": "start", "kind": "Start" },
                                        { "id": "agent-1", "kind": "Agent", "instructions": "Draft it.", "model": "qwen3:8b" },
                                        { "id": "debug-1", "kind": "Debug" },
                                        { "id": "agent-2", "kind": "Agent", "instructions": "Polish it.", "model": "qwen3:8b" },
                                        { "id": "end", "kind": "End" }
                                      ],
                                      "edges": [
                                        { "sourceId": "start", "targetId": "agent-1" },
                                        { "sourceId": "agent-1", "targetId": "debug-1" },
                                        { "sourceId": "debug-1", "targetId": "agent-2" },
                                        { "sourceId": "agent-2", "targetId": "end" }
                                      ]
                                    }
                                    """;

    /// <summary>Two Debug taps back to back, so the walk has to be transitive rather than one hop.</summary>
    public const string WithChainedDebug = """
                                           {
                                             "startText": "Draft it.",
                                             "nodes": [
                                               { "id": "start", "kind": "Start" },
                                               { "id": "agent-1", "kind": "Agent", "instructions": "Draft it.", "model": "qwen3:8b" },
                                               { "id": "debug-1", "kind": "Debug" },
                                               { "id": "debug-2", "kind": "Debug" },
                                               { "id": "end", "kind": "End" }
                                             ],
                                             "edges": [
                                               { "sourceId": "start", "targetId": "agent-1" },
                                               { "sourceId": "agent-1", "targetId": "debug-1" },
                                               { "sourceId": "debug-1", "targetId": "debug-2" },
                                               { "sourceId": "debug-2", "targetId": "end" }
                                             ]
                                           }
                                           """;

    /// <summary>A human wait before the end — the canvas's Continue, which becomes a single Approve decision.</summary>
    public const string WithPause = """
                                    {
                                      "startText": "Ship it?",
                                      "nodes": [
                                        { "id": "start", "kind": "Start" },
                                        { "id": "agent-1", "kind": "Agent", "instructions": "Prepare the change.", "model": "qwen3:8b" },
                                        { "id": "pause-1", "kind": "Pause", "label": "Sign off" },
                                        { "id": "end", "kind": "End" }
                                      ],
                                      "edges": [
                                        { "sourceId": "start", "targetId": "agent-1" },
                                        { "sourceId": "agent-1", "targetId": "pause-1" },
                                        { "sourceId": "pause-1", "targetId": "end" }
                                      ]
                                    }
                                    """;

    /// <summary>
    ///     A human wait BETWEEN two agents — the shape the S4 live round caught. The second agent has to read the
    ///     first one's answer, and a Pause's own output is only the approval.
    /// </summary>
    public const string AgentAcrossPause = """
                                           {
                                             "startText": "Ship it?",
                                             "nodes": [
                                               { "id": "start", "kind": "Start" },
                                               { "id": "agent-1", "kind": "Agent", "instructions": "Prepare the change.", "model": "qwen3:8b" },
                                               { "id": "pause-1", "kind": "Pause", "label": "Sign off" },
                                               { "id": "agent-2", "kind": "Agent", "instructions": "Announce it.", "model": "qwen3:8b" },
                                               { "id": "end", "kind": "End" }
                                             ],
                                             "edges": [
                                               { "sourceId": "start", "targetId": "agent-1" },
                                               { "sourceId": "agent-1", "targetId": "pause-1" },
                                               { "sourceId": "pause-1", "targetId": "agent-2" },
                                               { "sourceId": "agent-2", "targetId": "end" }
                                             ]
                                           }
                                           """;

    /// <summary>A Debug tap between the agent and the pause: the bypass has to start at the ELIDED chain's node.</summary>
    public const string DebugBeforePause = """
                                           {
                                             "startText": "Ship it?",
                                             "nodes": [
                                               { "id": "start", "kind": "Start" },
                                               { "id": "agent-1", "kind": "Agent", "instructions": "Prepare the change.", "model": "qwen3:8b" },
                                               { "id": "debug-1", "kind": "Debug" },
                                               { "id": "pause-1", "kind": "Pause" },
                                               { "id": "end", "kind": "End" }
                                             ],
                                             "edges": [
                                               { "sourceId": "start", "targetId": "agent-1" },
                                               { "sourceId": "agent-1", "targetId": "debug-1" },
                                               { "sourceId": "debug-1", "targetId": "pause-1" },
                                               { "sourceId": "pause-1", "targetId": "end" }
                                             ]
                                           }
                                           """;

    /// <summary>Two pauses back to back, so the walk back to a node that HAS content has to be transitive.</summary>
    public const string ChainedPauses = """
                                        {
                                          "startText": "Ship it?",
                                          "nodes": [
                                            { "id": "start", "kind": "Start" },
                                            { "id": "agent-1", "kind": "Agent", "instructions": "Prepare the change.", "model": "qwen3:8b" },
                                            { "id": "pause-1", "kind": "Pause" },
                                            { "id": "pause-2", "kind": "Pause" },
                                            { "id": "agent-2", "kind": "Agent", "instructions": "Announce it.", "model": "qwen3:8b" },
                                            { "id": "end", "kind": "End" }
                                          ],
                                          "edges": [
                                            { "sourceId": "start", "targetId": "agent-1" },
                                            { "sourceId": "agent-1", "targetId": "pause-1" },
                                            { "sourceId": "pause-1", "targetId": "pause-2" },
                                            { "sourceId": "pause-2", "targetId": "agent-2" },
                                            { "sourceId": "agent-2", "targetId": "end" }
                                          ]
                                        }
                                        """;

    /// <summary>A pause whose only predecessor is the Start node, whose output is the run's own input.</summary>
    public const string PauseAfterStart = """
                                          {
                                            "startText": "Ship it?",
                                            "nodes": [
                                              { "id": "start", "kind": "Start" },
                                              { "id": "pause-1", "kind": "Pause" },
                                              { "id": "agent-1", "kind": "Agent", "instructions": "Do it.", "model": "qwen3:8b" },
                                              { "id": "end", "kind": "End" }
                                            ],
                                            "edges": [
                                              { "sourceId": "start", "targetId": "pause-1" },
                                              { "sourceId": "pause-1", "targetId": "agent-1" },
                                              { "sourceId": "agent-1", "targetId": "end" }
                                            ]
                                          }
                                          """;

    /// <summary>A pause with nothing after it: there is no successor for a context edge to land on.</summary>
    public const string PauseWithNoSuccessor = """
                                               {
                                                 "startText": "Ship it?",
                                                 "nodes": [
                                                   { "id": "start", "kind": "Start" },
                                                   { "id": "agent-1", "kind": "Agent", "instructions": "Go.", "model": "qwen3:8b" },
                                                   { "id": "pause-1", "kind": "Pause" },
                                                   { "id": "end", "kind": "End" }
                                                 ],
                                                 "edges": [
                                                   { "sourceId": "start", "targetId": "agent-1" },
                                                   { "sourceId": "agent-1", "targetId": "pause-1" }
                                                 ]
                                               }
                                               """;

    /// <summary>
    ///     A pause successor that is NOT starved: <c>agent-3</c> also has an inbound edge from <c>agent-2</c>, which is
    ///     no Pause and already carries content, so no context edge is owed. Not a shape the canvas editor's linear
    ///     validator could author, but a stored row is only JSON and the mapper has to be total over it.
    /// </summary>
    public const string PauseSuccessorAlsoFedDirectly = """
                                                        {
                                                          "startText": "Ship it?",
                                                          "nodes": [
                                                            { "id": "start", "kind": "Start" },
                                                            { "id": "agent-1", "kind": "Agent", "instructions": "Prepare.", "model": "qwen3:8b" },
                                                            { "id": "agent-2", "kind": "Agent", "instructions": "Gather.", "model": "qwen3:8b" },
                                                            { "id": "pause-1", "kind": "Pause" },
                                                            { "id": "agent-3", "kind": "Agent", "instructions": "Announce.", "model": "qwen3:8b" },
                                                            { "id": "end", "kind": "End" }
                                                          ],
                                                          "edges": [
                                                            { "sourceId": "start", "targetId": "agent-1" },
                                                            { "sourceId": "start", "targetId": "agent-2" },
                                                            { "sourceId": "agent-1", "targetId": "pause-1" },
                                                            { "sourceId": "pause-1", "targetId": "agent-3" },
                                                            { "sourceId": "agent-2", "targetId": "agent-3" },
                                                            { "sourceId": "agent-3", "targetId": "end" }
                                                          ]
                                                        }
                                                        """;

    /// <summary>
    ///     A pause fed by TWO non-Pause nodes, so its nearest-ancestor set has two members and neither may be named:
    ///     edges from both would leave the successor's default <c>All</c> policy waiting on a branch never taken.
    /// </summary>
    public const string PauseWithTwoAncestors = """
                                                {
                                                  "startText": "Ship it?",
                                                  "nodes": [
                                                    { "id": "start", "kind": "Start" },
                                                    { "id": "agent-1", "kind": "Agent", "instructions": "Draft.", "model": "qwen3:8b" },
                                                    { "id": "agent-2", "kind": "Agent", "instructions": "Review.", "model": "qwen3:8b" },
                                                    { "id": "pause-1", "kind": "Pause" },
                                                    { "id": "end", "kind": "End" }
                                                  ],
                                                  "edges": [
                                                    { "sourceId": "start", "targetId": "agent-1" },
                                                    { "sourceId": "start", "targetId": "agent-2" },
                                                    { "sourceId": "agent-1", "targetId": "pause-1" },
                                                    { "sourceId": "agent-2", "targetId": "pause-1" },
                                                    { "sourceId": "pause-1", "targetId": "end" }
                                                  ]
                                                }
                                                """;

    /// <summary>
    ///     A canvas kind the mapper writes through verbatim, which lands a node of kind <c>Condition</c> in the mapped
    ///     document. Naming it as the context ancestor would give that node a second unconditional out-edge, which the
    ///     validator refuses — so the advice is withheld even though the pause successor is starved.
    /// </summary>
    public const string PauseBehindAConditionKind = """
                                                    {
                                                      "startText": "Ship it?",
                                                      "nodes": [
                                                        { "id": "start", "kind": "Start" },
                                                        { "id": "branch", "kind": "Condition" },
                                                        { "id": "pause-1", "kind": "Pause" },
                                                        { "id": "end", "kind": "End" }
                                                      ],
                                                      "edges": [
                                                        { "sourceId": "start", "targetId": "branch" },
                                                        { "sourceId": "branch", "targetId": "pause-1" },
                                                        { "sourceId": "pause-1", "targetId": "end" }
                                                      ]
                                                    }
                                                    """;

    /// <summary>An agent carrying the provider hint that has no destination in the new Agent config.</summary>
    public const string WithModelProfile = """
                                           {
                                             "startText": "Go.",
                                             "nodes": [
                                               { "id": "start", "kind": "Start" },
                                               { "id": "agent-1", "kind": "Agent", "instructions": "Go.", "model": "qwen3:8b", "modelProfile": "reasoning-heavy" },
                                               { "id": "end", "kind": "End" }
                                             ],
                                             "edges": [
                                               { "sourceId": "start", "targetId": "agent-1" },
                                               { "sourceId": "agent-1", "targetId": "end" }
                                             ]
                                           }
                                           """;

    /// <summary>Ids the key charset cannot take verbatim: spaces, punctuation, and one that sanitizes to nothing.</summary>
    public const string AwkwardIds = """
                                     {
                                       "startText": "Go.",
                                       "nodes": [
                                         { "id": "the start!", "kind": "Start" },
                                         { "id": "agent one (draft)", "kind": "Agent", "instructions": "Draft.", "model": "qwen3:8b" },
                                         { "id": "!!!", "kind": "End" }
                                       ],
                                       "edges": [
                                         { "sourceId": "the start!", "targetId": "agent one (draft)" },
                                         { "sourceId": "agent one (draft)", "targetId": "!!!" }
                                       ]
                                     }
                                     """;

    /// <summary>Two nodes sharing one id — the canvas validator refused it, so a stored row carrying it is damaged.</summary>
    public const string DuplicateIds = """
                                       {
                                         "startText": "Go.",
                                         "nodes": [
                                           { "id": "start", "kind": "Start" },
                                           { "id": "agent-1", "kind": "Agent", "instructions": "First.", "model": "qwen3:8b" },
                                           { "id": "agent-1", "kind": "Agent", "instructions": "Second.", "model": "qwen3:8b" },
                                           { "id": "end", "kind": "End" }
                                         ],
                                         "edges": [
                                           { "sourceId": "start", "targetId": "agent-1" },
                                           { "sourceId": "agent-1", "targetId": "end" }
                                         ]
                                       }
                                       """;

    /// <summary>A Debug node with nothing after it: impossible under the old validator, and not trusted.</summary>
    public const string DebugWithNoSuccessor = """
                                               {
                                                 "startText": "Go.",
                                                 "nodes": [
                                                   { "id": "start", "kind": "Start" },
                                                   { "id": "agent-1", "kind": "Agent", "instructions": "Go.", "model": "qwen3:8b" },
                                                   { "id": "debug-1", "kind": "Debug" },
                                                   { "id": "end", "kind": "End" }
                                                 ],
                                                 "edges": [
                                                   { "sourceId": "start", "targetId": "agent-1" },
                                                   { "sourceId": "agent-1", "targetId": "debug-1" }
                                                 ]
                                               }
                                               """;

    /// <summary>A kind no canvas build ever wrote, which is exactly why the mapper must not assume the set is closed.</summary>
    public const string UnknownKind = """
                                      {
                                        "startText": "Go.",
                                        "nodes": [
                                          { "id": "start", "kind": "Start" },
                                          { "id": "switch-1", "kind": "Switch" },
                                          { "id": "end", "kind": "End" }
                                        ],
                                        "edges": [
                                          { "sourceId": "start", "targetId": "switch-1" },
                                          { "sourceId": "switch-1", "targetId": "end" }
                                        ]
                                      }
                                      """;

    /// <summary>An agent with neither model nor instructions, and no seed text: every optional field absent at once.</summary>
    public const string BareFields = """
                                     {
                                       "nodes": [
                                         { "id": "start", "kind": "Start" },
                                         { "id": "agent-1", "kind": "Agent" },
                                         { "id": "end", "kind": "End" }
                                       ],
                                       "edges": [
                                         { "sourceId": "start", "targetId": "agent-1" },
                                         { "sourceId": "agent-1", "targetId": "end" }
                                       ]
                                     }
                                     """;

    /// <summary>An edge naming a node the graph does not declare.</summary>
    public const string DanglingEdge = """
                                       {
                                         "startText": "Go.",
                                         "nodes": [
                                           { "id": "start", "kind": "Start" },
                                           { "id": "agent-1", "kind": "Agent", "instructions": "Go.", "model": "qwen3:8b" },
                                           { "id": "end", "kind": "End" }
                                         ],
                                         "edges": [
                                           { "sourceId": "start", "targetId": "agent-1" },
                                           { "sourceId": "agent-1", "targetId": "ghost" },
                                           { "sourceId": "agent-1", "targetId": "end" }
                                         ]
                                       }
                                       """;

    /// <summary>
    ///     A Debug node whose only outgoing edge points at itself. Impossible under the old validator, and the walk
    ///     that elides Debug nodes stops on its own seen-set rather than on a missing successor.
    /// </summary>
    public const string DebugPointingAtItself = """
                                                {
                                                  "startText": "Go.",
                                                  "nodes": [
                                                    { "id": "start", "kind": "Start" },
                                                    { "id": "agent-1", "kind": "Agent", "instructions": "Go.", "model": "qwen3:8b" },
                                                    { "id": "debug-1", "kind": "Debug" },
                                                    { "id": "end", "kind": "End" }
                                                  ],
                                                  "edges": [
                                                    { "sourceId": "start", "targetId": "agent-1" },
                                                    { "sourceId": "agent-1", "targetId": "debug-1" },
                                                    { "sourceId": "debug-1", "targetId": "debug-1" }
                                                  ]
                                                }
                                                """;

    /// <summary>
    ///     A JSON null sitting inside <c>nodes</c> and another inside <c>edges</c>. Valid JSON, so nothing upstream
    ///     rejects it, and it deserializes to null ELEMENTS the mapper would otherwise dereference.
    /// </summary>
    public const string NullEntries = """
                                      {
                                        "startText": "Go.",
                                        "nodes": [
                                          { "id": "start", "kind": "Start" },
                                          null,
                                          { "id": "agent-1", "kind": "Agent", "instructions": "Go.", "model": "qwen3:8b" },
                                          { "id": "end", "kind": "End" }
                                        ],
                                        "edges": [
                                          { "sourceId": "start", "targetId": "agent-1" },
                                          null,
                                          { "sourceId": "agent-1", "targetId": "end" }
                                        ]
                                      }
                                      """;

    /// <summary>Valid JSON that is not a canvas graph at all. The reader lets it through; the mapper must not throw.</summary>
    public const string NotAGraph = """[1, 2, 3]""";

    /// <summary>An empty object: every member absent, which deserializes to a graph with no nodes.</summary>
    public const string Empty = """{}""";

    /// <summary>The one member set to the wrong JSON type, which is where the deserializer itself gives up.</summary>
    public const string NodesNotAnArray = """{"startText":"Go.","nodes":7}""";
}
