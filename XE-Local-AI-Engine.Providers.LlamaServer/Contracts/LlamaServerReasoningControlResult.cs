namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>What llama-server answered to a <c>reasoning_end</c> control request.</summary>
public sealed class LlamaServerReasoningControlResult
{
    /// <summary>The server ended the reasoning block.</summary>
    public required bool Success { get; init; }

    /// <summary>The server's own explanation, for logs only: it is not sanitized for display.</summary>
    public string? Message { get; init; }
}
