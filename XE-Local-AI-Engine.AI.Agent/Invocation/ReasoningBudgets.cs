namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     The llama.cpp thinking budget in tokens per reasoning-effort level, plus the level a turn with no effort gets.
/// </summary>
/// <remarks>
///     Node settings with these shipped defaults (operator decision, model-matrix 2026-10-04): a flat 8192 for an
///     unspecified effort let a 0.8B reason for minutes over a five-word greeting. <c>xhigh</c> takes the
///     <see cref="High" /> rung. See docs/wiki/04-agent-mode.md ("The reasoning-effort matrix and the thinking budget").
/// </remarks>
public sealed record ReasoningBudgets
{
    /// <summary>The shipped defaults.</summary>
    public static ReasoningBudgets Default { get; } = new()
    {
        Minimal = 1024,
        Low = 2048,
        Medium = 8192,
        High = 24576,
        UnspecifiedEffort = "low"
    };

    /// <summary>
    ///     The fixed ladder from before the budgets were node settings (a blank effort took medium). A frozen benchmark runs
    ///     under it, so saving node settings cannot change its replay.
    /// </summary>
    public static ReasoningBudgets Frozen { get; } = new()
    {
        Minimal = 1024,
        Low = 2048,
        Medium = 8192,
        High = 24576,
        UnspecifiedEffort = "medium"
    };

    /// <summary>Budget for <c>minimal</c>.</summary>
    public required int Minimal { get; init; }

    /// <summary>Budget for <c>low</c>.</summary>
    public required int Low { get; init; }

    /// <summary>Budget for <c>medium</c>.</summary>
    public required int Medium { get; init; }

    /// <summary>Budget for <c>high</c> and <c>xhigh</c>.</summary>
    public required int High { get; init; }

    /// <summary>The level whose budget a blank effort gets: minimal, low, medium or high.</summary>
    public required string UnspecifiedEffort { get; init; }
}
