namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>The scalar types one member of a session's output object may declare.</summary>
internal enum WorkSessionOutputType
{
    String,
    Number,
    Integer,
    Boolean
}

/// <summary>One member of a session's output object.</summary>
internal sealed class WorkSessionOutputProperty
{
    public required string Name { get; init; }

    public required WorkSessionOutputType Type { get; init; }

    public required bool Required { get; init; }

    /// <summary>The values a string member may take, or null for any string.</summary>
    public required IReadOnlyList<string>? Enum { get; init; }

    /// <summary>The longest a string member may be, or null for the tool's own text bound.</summary>
    public required int? MaxLength { get; init; }
}
