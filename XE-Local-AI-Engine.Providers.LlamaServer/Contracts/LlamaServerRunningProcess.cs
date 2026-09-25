namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>One live, servable <c>(model, role)</c> llama-server process, read from the supervisor's in-memory table.</summary>
public sealed class LlamaServerRunningProcess
{
    /// <summary>Model the process serves.</summary>
    public required string ModelName { get; init; }

    /// <summary>Role the process serves.</summary>
    public required ModelRole Role { get; init; }

    /// <summary>When the process was last ensured or reused, stamped per request rather than per token.</summary>
    public required DateTimeOffset LastUsedUtc { get; init; }
}
