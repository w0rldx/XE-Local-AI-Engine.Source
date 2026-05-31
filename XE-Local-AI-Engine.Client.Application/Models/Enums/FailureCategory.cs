namespace XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     Enumerates supported failure category values.
/// </summary>
public enum FailureCategory
{
    Cancelled = 0,
    Timeout = 1,
    AgentRuntime = 2,
    ProviderUnreachable = 3,
    Unexpected = 4,
    AgentToolCall = 5,
    HashMismatch = 6,
    ModelUnavailable = 7
}
