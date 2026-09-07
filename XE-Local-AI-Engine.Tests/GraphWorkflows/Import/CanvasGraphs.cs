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
