namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools;

using System.Text.RegularExpressions;

/// <summary>
///     Typed projection of the <c>run_in_agent_home</c> JSON arguments. The
///     <see cref="MetadataToolFunction" /> bridge stays JSON-in / JSON-out, so the handler deserializes into this
///     record and validates it against the tool constraints before any execution — the schema advertised to the
///     model is advisory; this validation is authoritative.
/// </summary>
internal sealed record AgentHomeRunToolRequest
{
    public string? Goal { get; init; }

    public IReadOnlyList<string>? SelectedFolderIds { get; init; }

    public string? RuntimeProfile { get; init; }

    public string? Persona { get; init; }

    public IReadOnlyList<string>? AllowedActions { get; init; }
}

/// <summary>Validates an <see cref="AgentHomeRunToolRequest" /> against the AgentHome tool constraints.</summary>
internal static partial class AgentHomeRunToolRequestValidator
{
    private const int GoalMaxLength = 4000;
    private const int MaxSelectedFolders = 8;

    /// <summary>
    ///     The authoritative selected-folder-id pattern: a lowercase alias or a 36-character GUID. It is a
    ///     <see langword="const" /> so the <see cref="GeneratedRegexAttribute" /> below and the copy embedded in
    ///     <see cref="AgentHomeToolDefinition.ParameterSchema" /> have ONE source an equality test can compare against
    ///     — see <c>AgentHomeToolSchemaContractTests</c>.
    ///     <para>
    ///         The alternation MUST stay inside one pair of anchors. It means the same thing to .NET either way, but
    ///         llama.cpp's GBNF converter treats an anchor that is not the pattern's first/last character as a literal
    ///         character, so <c>^a$|^b$</c> compiles into a grammar that forces a literal <c>$</c> onto every value the
    ///         model emits. See the comment on <see cref="AgentHomeToolDefinition.ParameterSchema" />.
    ///     </para>
    /// </summary>
    internal const string SelectedFolderIdPattern = "^([a-z0-9][a-z0-9-]{0,63}|[0-9a-fA-F-]{36})$";

    // `propose_memory` was REMOVED, not renamed: the node collected proposals from the sandbox and then discarded
    // them, so offering the value advertised a capability that silently no-opped. Persisting a proposal into the
    // existing adaptive-memory Suggested pipeline is a later slice; until it lands, the schema must not claim it.
    private static readonly string[] AllowedActionValues =
    [
        "read_workspace", "write_workspace", "run_commands", "export_patch"
    ];

    private static readonly string[] RuntimeProfileValues = ["dotnet-agent-home"];
    private static readonly string[] PersonaValues = ["primary/main"];

    public static IReadOnlyList<string> Validate(AgentHomeRunToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();

        ValidateGoal(request.Goal, errors);
        ValidateSelectedFolderIds(request.SelectedFolderIds, errors);
        ValidateAllowedActions(request.AllowedActions, errors);
        ValidateClosedEnum(request.RuntimeProfile, RuntimeProfileValues, "runtimeProfile", errors);
        ValidateClosedEnum(request.Persona, PersonaValues, "persona", errors);

        return errors;
    }

    private static void ValidateGoal(string? goal, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            errors.Add("'goal' is required and must be a non-empty string.");
            return;
        }

        if (goal.Length > GoalMaxLength)
        {
            errors.Add($"'goal' must be at most {GoalMaxLength} characters.");
        }
    }

    private static void ValidateSelectedFolderIds(IReadOnlyList<string>? selectedFolderIds, List<string> errors)
    {
        if (selectedFolderIds is null || selectedFolderIds.Count == 0)
        {
            errors.Add("'selectedFolderIds' is required and must contain at least one folder id.");
            return;
        }

        if (selectedFolderIds.Count > MaxSelectedFolders)
        {
            errors.Add($"'selectedFolderIds' must contain at most {MaxSelectedFolders} folder ids.");
        }

        errors.AddRange(selectedFolderIds
                        .Where(static folderId => string.IsNullOrEmpty(folderId) || !SelectedFolderIdRegex().IsMatch(folderId))
                        .Select(static folderId => $"'selectedFolderIds' contains an invalid id: '{folderId}'."));
    }

    private static void ValidateAllowedActions(IReadOnlyList<string>? allowedActions, List<string> errors)
    {
        if (allowedActions is null || allowedActions.Count == 0)
        {
            errors.Add("'allowedActions' is required and must contain at least one action.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in allowedActions)
        {
            if (!AllowedActionValues.Contains(action, StringComparer.Ordinal))
            {
                errors.Add($"'allowedActions' contains an unsupported action: '{action}'.");
            }

            if (!seen.Add(action))
            {
                errors.Add($"'allowedActions' contains a duplicate action: '{action}'.");
            }
        }
    }

    private static void ValidateClosedEnum(string? value, string[] allowed, string field, List<string> errors)
    {
        if (value is not null && !allowed.Contains(value, StringComparer.Ordinal))
        {
            errors.Add($"'{field}' must be one of: {string.Join(", ", allowed)}.");
        }
    }

    // ExplicitCapture, not a `(?:` group in the pattern itself: the group exists only to scope the alternation under the
    // anchors, nothing reads it, and keeping the pattern text free of engine-specific syntax is what lets the schema
    // copy stay byte-identical for every provider that compiles it (MA0023 is satisfied by the option, not the text).
    [GeneratedRegex(SelectedFolderIdPattern, RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SelectedFolderIdRegex();
}
