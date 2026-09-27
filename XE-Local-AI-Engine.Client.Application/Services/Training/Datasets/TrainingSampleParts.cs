namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>
///     Reading a sample trajectory's tool calls. v1 samples are single-call BY CONSTRUCTION —
///     <see cref="TeacherSampleRecordV1" /> carries one <c>toolName</c>.
/// </summary>
/// <remarks>
///     A trajectory with more than one tool part is a sample nothing in the module can grade: the scorer compares
///     ONE expectation against what the model called, so both ends refuse it rather than silently taking the first
///     call. A kept-as-a-property helper is deliberately avoided — <see cref="TrainingSampleContentV1" /> is
///     persisted AND reused verbatim as a wire DTO, so a computed member would change both schemas.
/// </remarks>
public static class TrainingSampleParts
{
    /// <summary>What an operator is told when a sample demonstrates more than one call.</summary>
    public const string MultiCallUnsupportedReason = "Multi-call samples are not supported: the sample demonstrates more than one tool call.";

    /// <summary>The trajectory's tool parts, in order. A part without a tool name is not a call.</summary>
    public static IReadOnlyList<TrainingSamplePartV1> ToolCalls(IEnumerable<TrainingSamplePartV1> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Where(part => string.Equals(part.Kind, "tool", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(part.ToolName))
                    .ToArray();
    }

    public static bool IsMultiCall(IEnumerable<TrainingSamplePartV1> parts) =>
        ToolCalls(parts).Count > 1;
}
