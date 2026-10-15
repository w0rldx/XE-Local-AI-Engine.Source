namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>A custom tool a graph Tool node may run with nobody present: the stored record and its kind's executor, unwrapped.</summary>
internal sealed class UnattendedCustomTool
{
    public required CustomToolRecord Record { get; init; }

    public required ICustomToolExecutor Executor { get; init; }

    /// <summary>The compiled argument schema; a closed empty object, since only a <c>Fixed</c> tool qualifies.</summary>
    public required string ParameterSchema { get; init; }

    public required ToolCategory Category { get; init; }
}
