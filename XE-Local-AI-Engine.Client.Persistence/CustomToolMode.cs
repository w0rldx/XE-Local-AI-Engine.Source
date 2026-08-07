namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Parameter posture of a custom tool. <see cref="Fixed" /> runs a verbatim, operator-authored invocation the model
///     cannot alter; <see cref="Parameterized" /> declares typed inputs the model fills in at call time. The mode gates
///     the approval floor downstream — a Fixed tool may be session-approved, a Parameterized one is once-or-deny only.
/// </summary>
public enum CustomToolMode
{
    Fixed = 0,
    Parameterized = 1
}
