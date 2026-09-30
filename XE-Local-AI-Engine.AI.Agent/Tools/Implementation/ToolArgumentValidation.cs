namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

/// <summary>Outcome of validating (and coercing) a model's tool arguments against the tool's JSON schema.</summary>
internal readonly record struct ToolArgumentValidation(bool IsValid, bool WasCoerced, string? Reason)
{
    public static ToolArgumentValidation Valid(bool wasCoerced = false)
    {
        return new ToolArgumentValidation(true, wasCoerced, null);
    }

    public static ToolArgumentValidation Invalid(string reason)
    {
        return new ToolArgumentValidation(false, false, reason);
    }
}
