namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

/// <summary>
///     How a streamed llama-server completion advertises that its reasoning can be ended early ("Answer now"), and
///     the outcome of asking it to.
/// </summary>
/// <remarks>
///     A request that carried <c>reasoning_control: true</c> stamps the first update of its stream with
///     <see cref="EndpointKey" /> (the server's base address); the update's <see cref="ChatResponseUpdate.ResponseId" /> is
///     the <c>chatcmpl-…</c> id the control route needs; <see cref="TryRead" /> recovers both. Route and body verified
///     against the pinned build's server README (b10201, "POST /v1/chat/completions/control").
/// </remarks>
public static class LlamaServerReasoningControl
{
    /// <summary>The in-process update property carrying the base address of the server that armed the control.</summary>
    public const string EndpointKey = "xe.llama.reasoning_control_endpoint";

    /// <summary>
    ///     Reads the armed control from <paramref name="rawRepresentation" />: the MEAI update itself, or the raw
    ///     representation of the agent update wrapping it (the same input <see cref="LlamaServerGenerationTimings.TryRead" />
    ///     takes).
    /// </summary>
    public static bool TryRead(object? rawRepresentation, [NotNullWhen(true)] out Uri? baseAddress, [NotNullWhen(true)] out string? completionId)
    {
        if (rawRepresentation is ChatResponseUpdate chatUpdate
            && chatUpdate.AdditionalProperties?.TryGetValue(EndpointKey, out var raw) == true
            && raw is Uri endpoint
            && !string.IsNullOrEmpty(chatUpdate.ResponseId))
        {
            baseAddress = endpoint;
            completionId = chatUpdate.ResponseId;
            return true;
        }

        baseAddress = null;
        completionId = null;
        return false;
    }
}
