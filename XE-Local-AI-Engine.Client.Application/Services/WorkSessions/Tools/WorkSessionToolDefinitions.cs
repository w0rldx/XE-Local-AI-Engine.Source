namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Name / description / parameter-schema constants for the four work-session state tools. The handlers advertise
///     their model-visible schema from here and the offer provider merges the same descriptors into the profile offer,
///     so what the model is offered can never drift from what the handler validates.
///     <para>
///         The schemas carry no <c>maxLength</c>. Bounds over 1024 are stripped from the llama.cpp wire by the grammar
///         compatibility pass, and the whole offered <c>tools</c> array compiles into ONE GBNF grammar with a shared
///         repetition ceiling — so a Research session, which offers seven schemas on top of these four, is exactly the
///         case that ceiling bites. The length bounds below are enforced by the handlers, which are authoritative
///         anyway.
///     </para>
/// </summary>
internal static class WorkSessionToolDefinitions
{
    /// <summary>The cap on a whole tool-call argument payload, checked before any parse.</summary>
    public const int MaxJsonArgumentsLength = 128 * 1024;

    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 4000;
    public const int ReferenceMaxLength = 500;
    public const int NameMaxLength = 120;
    public const int MaxPlanOperations = 20;

    public static class UpdateWorkPlan
    {
        public const string ToolName = "update_work_plan";

        public const string Description =
            "Keep the work session's plan current. Send a batch of operations: add a new task, update one, mark one "
            + "complete, or drop one that is no longer needed. Use 'add' for work you have discovered, set status "
            + "'Active' on the task you are working on right now, and 'complete' as soon as a task is genuinely done. "
            + "Task ids come from the work session state block, and the result of a call names the id of every "
            + "task its 'add' operations created — use those ids to update, complete or drop a task you added "
            + "in this same step.";

        public const string ParameterSchema = """
                                              {
                                                "type": "object",
                                                "additionalProperties": false,
                                                "required": ["operations"],
                                                "properties": {
                                                  "operations": {
                                                    "type": "array",
                                                    "minItems": 1,
                                                    "maxItems": 20,
                                                    "items": {
                                                      "type": "object",
                                                      "additionalProperties": false,
                                                      "required": ["op"],
                                                      "properties": {
                                                        "op": { "type": "string", "enum": ["add", "update", "complete", "drop"] },
                                                        "taskId": { "type": "string" },
                                                        "title": { "type": "string" },
                                                        "name": { "type": "string", "description": "Alias for title." },
                                                        "text": { "type": "string", "description": "Alias for title." },
                                                        "summary": { "type": "string", "description": "Alias for title." },
                                                        "detail": { "type": "string" },
                                                        "status": { "type": "string", "enum": ["Planned", "Active", "Blocked", "Done", "Dropped"] },
                                                        "blockedReason": { "type": "string" },
                                                        "parentTaskId": { "type": "string" }
                                                      }
                                                    }
                                                  }
                                                }
                                              }
                                              """;

        /// <summary>Handed back verbatim when the arguments would not read: a model recovers from a shape it can copy.</summary>
        public const string ExampleArguments = """{"operations":[{"op":"add","title":"Investigate X"}]}""";
    }

    public static class RecordFinding
    {
        public const string ToolName = "record_finding";

        public const string Description =
            "Record something you learned so it survives into later steps and into the session's report. Use kind "
            + "'Finding' for a fact, 'Evidence' for a quote or excerpt that backs one up, 'Decision' for a choice you "
            + "made and why, and 'OpenQuestion' for something still unresolved. Put the citation, tool-call id or "
            + "document reference in sourceRef.";

        public const string ParameterSchema = """
                                              {
                                                "type": "object",
                                                "additionalProperties": false,
                                                "required": ["kind", "text"],
                                                "properties": {
                                                  "kind": { "type": "string", "enum": ["Finding", "Evidence", "Decision", "OpenQuestion"] },
                                                  "text": { "type": "string", "minLength": 1 },
                                                  "sourceRef": { "type": "string" },
                                                  "taskId": { "type": "string" },
                                                  "supersedesId": { "type": "string" }
                                                }
                                              }
                                              """;

        /// <summary>Handed back verbatim when the arguments would not read: a model recovers from a shape it can copy.</summary>
        public const string ExampleArguments = """{"kind":"Finding","text":"The runtime pins llama.cpp b10201.","sourceRef":"docs/agent-knowledge.md"}""";
    }

    public static class SaveArtifact
    {
        public const string ToolName = "save_artifact";

        public const string Description =
            "Save a durable output of the session — a report, a note, a file or a patch. Provide exactly one of text "
            + "(UTF-8) or base64 (binary). Saving under a name that already exists replaces the earlier artifact.";

        public const string ParameterSchema = """
                                              {
                                                "type": "object",
                                                "additionalProperties": false,
                                                "required": ["name", "mediaType", "kind"],
                                                "properties": {
                                                  "name": { "type": "string", "minLength": 1 },
                                                  "mediaType": { "type": "string", "minLength": 1 },
                                                  "kind": { "type": "string", "enum": ["Report", "Note", "File", "Patch"] },
                                                  "text": { "type": "string" },
                                                  "base64": { "type": "string" }
                                                }
                                              }
                                              """;

        /// <summary>Handed back verbatim when the arguments would not read: a model recovers from a shape it can copy.</summary>
        public const string ExampleArguments = """{"name":"report.md","mediaType":"text/markdown","kind":"Report","text":"The whole report."}""";
    }

    public static class CompleteWorkSession
    {
        public const string ToolName = "complete_work_session";

        public const string Description =
            "Close the session and hand in a summary of what you did and found. Call this only when every task that "
            + "matters is Done or Dropped and the findings tell the whole story. If you could NOT meet the objective, "
            + "call it anyway with objectiveMet false and say in the summary what is missing and why — an honest "
            + "unmet close is read as unmet, while a silent one is read as success. The session finishes at the end of "
            + "this turn, so say anything else you still want to say before calling it.";

        // objectiveMet is a plain boolean on purpose: the whole offered tools array compiles into ONE GBNF grammar for
        // llama.cpp, and a boolean adds no repetition bound to it at all. It is optional, and absent means met, so
        // every transcript recorded before it existed still reads as the completion it was.
        public const string ParameterSchema = """
                                              {
                                                "type": "object",
                                                "additionalProperties": false,
                                                "required": ["summary"],
                                                "properties": {
                                                  "summary": { "type": "string", "minLength": 1 },
                                                  "objectiveMet": { "type": "boolean" }
                                                }
                                              }
                                              """;

        /// <summary>Handed back verbatim when the arguments would not read: a model recovers from a shape it can copy.</summary>
        public const string ExampleArguments = """{"summary":"What the session achieved.","objectiveMet":true}""";
    }

    /// <summary>Every work-session tool name, in offer order.</summary>
    public static readonly IReadOnlyList<string> ToolNames =
    [
        UpdateWorkPlan.ToolName,
        RecordFinding.ToolName,
        SaveArtifact.ToolName,
        CompleteWorkSession.ToolName
    ];
}

/// <summary>
///     The four state tools as offer descriptors, so the offer provider merges exactly what the handlers advertise.
///     <para>
///         Every one is <see cref="ToolCategory.WriteExecute" />: they all write durable session rows, and that is the
///         only write category the enum has. Calling them <see cref="ToolCategory.ReadLocal" /> would hide the write
///         from a category-based operator policy, which is worse than the consequence of being honest — tightening
///         <c>WriteExecute</c> makes every recorded finding need an approval click.
///     </para>
///     <para>
///         <c>RequiresApproval</c> is false on all four: a session records dozens of findings, and a prompt per finding
///         would make an unattended run impossible. Their blast radius is the session's own rows.
///     </para>
/// </summary>
internal static class WorkSessionToolCatalog
{
    public static readonly IReadOnlyList<LocalChatToolDescriptor> Descriptors =
    [
        new(WorkSessionToolDefinitions.UpdateWorkPlan.ToolName,
            WorkSessionToolDefinitions.UpdateWorkPlan.Description,
            WorkSessionToolDefinitions.UpdateWorkPlan.ParameterSchema,
            RequiresApproval: false,
            ToolCategory.WriteExecute),
        new(WorkSessionToolDefinitions.RecordFinding.ToolName,
            WorkSessionToolDefinitions.RecordFinding.Description,
            WorkSessionToolDefinitions.RecordFinding.ParameterSchema,
            RequiresApproval: false,
            ToolCategory.WriteExecute),
        new(WorkSessionToolDefinitions.SaveArtifact.ToolName,
            WorkSessionToolDefinitions.SaveArtifact.Description,
            WorkSessionToolDefinitions.SaveArtifact.ParameterSchema,
            RequiresApproval: false,
            ToolCategory.WriteExecute),
        new(WorkSessionToolDefinitions.CompleteWorkSession.ToolName,
            WorkSessionToolDefinitions.CompleteWorkSession.Description,
            WorkSessionToolDefinitions.CompleteWorkSession.ParameterSchema,
            RequiresApproval: false,
            ToolCategory.WriteExecute)
    ];
}
