namespace XE_Local_AI_Engine.Client.Services.Coder.Tools;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Worker-side name / description / parameter-schema constants for the three read-only coder tools
///     (<c>list_files</c>, <c>read_file</c>, <c>search_text</c>). Each handler advertises its model-visible schema from
///     here and the offer provider merges the same descriptors into the loopback offer, so the schema the model is
///     offered can never drift from what the handler validates. The schemas are advisory to the model; the handlers'
///     own validation is authoritative.
/// </summary>
internal static class CoderToolDefinition
{
    public const string ListFilesToolName = "list_files";

    public const string ListFilesDescription =
        "List files and folders in the read-only project workspace. Returns workspace-relative paths only; secrets and "
        + "heavy generated directories are excluded.";

    public const string ListFilesParameterSchema = """
                                                   {
                                                     "type": "object",
                                                     "additionalProperties": false,
                                                     "properties": {
                                                       "path": { "type": "string", "maxLength": 4096 },
                                                       "glob": { "type": "string", "maxLength": 512 },
                                                       "maxResults": { "type": "integer", "minimum": 1, "maximum": 5000 }
                                                     }
                                                   }
                                                   """;

    public const string ReadFileToolName = "read_file";

    public const string ReadFileDescription =
        "Read a UTF-8 text file from the read-only project workspace. Optionally read a line range. Binary files are "
        + "refused and oversized files are truncated.";

    public const string ReadFileParameterSchema = """
                                                  {
                                                    "type": "object",
                                                    "additionalProperties": false,
                                                    "required": ["path"],
                                                    "properties": {
                                                      "path": { "type": "string", "minLength": 1, "maxLength": 4096 },
                                                      "startLine": { "type": "integer", "minimum": 1 },
                                                      "endLine": { "type": "integer", "minimum": 1 }
                                                    }
                                                  }
                                                  """;

    public const string SearchTextToolName = "search_text";

    public const string SearchTextDescription =
        "Search the read-only project workspace for a text or regex pattern. Returns matches as "
        + "relative/path:line: text; secret files and directories are excluded.";

    public const string SearchTextParameterSchema = """
                                                    {
                                                      "type": "object",
                                                      "additionalProperties": false,
                                                      "required": ["pattern"],
                                                      "properties": {
                                                        "pattern": { "type": "string", "minLength": 1, "maxLength": 1024 },
                                                        "path": { "type": "string", "maxLength": 4096 },
                                                        "isRegex": { "type": "boolean" },
                                                        "maxMatches": { "type": "integer", "minimum": 1, "maximum": 2000 }
                                                      }
                                                    }
                                                    """;

    /// <summary>
    ///     The model-visible descriptors for the three coder tools — name + schema + approval flag. The offer provider
    ///     consumes these to merge the coder tools into the capability-gated loopback offer. All three are
    ///     auto-execute (<c>RequiresApproval = false</c>): they are read-only, workspace-confined,
    ///     secret-filtered, and capped, so the confinement controls are the safety boundary, not a per-call prompt.
    /// </summary>
    public static IReadOnlyList<CoderToolDescriptor> Descriptors { get; } =
    [
        new CoderToolDescriptor(ListFilesToolName, ListFilesDescription, ListFilesParameterSchema),
        new CoderToolDescriptor(ReadFileToolName, ReadFileDescription, ReadFileParameterSchema),
        new CoderToolDescriptor(SearchTextToolName, SearchTextDescription, SearchTextParameterSchema)
    ];
}

/// <summary>
///     Offer-side metadata for a single coder tool. Mirrors the shape the offer provider needs (name + schema +
///     approval flag + risk category); <see cref="RequiresApproval" /> is always <see langword="false" /> for coder
///     tools and <see cref="Category" /> is always <see cref="ToolCategory.ReadLocal" /> (read-only, workspace-confined
///     file reads).
/// </summary>
internal sealed record CoderToolDescriptor(string Name, string Description, string ParameterSchema)
{
    /// <summary>Coder read tools never require approval.</summary>
    public bool RequiresApproval { get; }

    /// <summary>Coder tools are read-only, workspace-confined node-local reads.</summary>
    public ToolCategory Category { get; } = ToolCategory.ReadLocal;
}
