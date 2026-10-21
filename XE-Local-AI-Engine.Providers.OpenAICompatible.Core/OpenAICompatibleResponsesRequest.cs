namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

using Microsoft.Extensions.AI;
using OpenAI.Responses;

/// <summary>Builds the per-call options that send a Responses request statelessly.</summary>
/// <remarks>
///     MEAI's Responses adapter uses the <see cref="ChatOptions.RawRepresentationFactory" /> result as its base
///     <see cref="CreateResponseOptions" /> and fills only what that leaves unset, so a reasoning block set here wins over
///     <see cref="ChatOptions.Reasoning" />. A <see langword="null" /> options object skips the factory entirely, which is
///     why this always returns an instance.
/// </remarks>
public static class OpenAICompatibleResponsesRequest
{
    /// <summary>
    ///     Returns <paramref name="options" />, or a new instance, whose raw factory sends <c>store=false</c> and, when
    ///     <paramref name="includeReasoning" />, an auto reasoning summary, the encrypted-reasoning include and the effort.
    /// </summary>
    /// <param name="options">The per-call options to decorate, already cloned by the caller; a new instance when null.</param>
    /// <param name="includeReasoning">False omits the whole reasoning block and the include, which a non-reasoning model may reject.</param>
    /// <param name="effort">The effort level, or <see langword="null" /> for the model's default; ignored without reasoning.</param>
    /// <param name="parallelToolCalls">The <c>parallel_tool_calls</c> value, or <see langword="null" /> to leave it to MEAI.</param>
#pragma warning disable OPENAI001 // The Responses option types are experimental in the pinned OpenAI SDK.
    public static ChatOptions Stateless(ChatOptions? options,
        bool includeReasoning,
        ResponseReasoningEffortLevel? effort,
        bool? parallelToolCalls)
    {
        var result = options ?? new ChatOptions();
        result.RawRepresentationFactory = _ =>
        {
            // No PreviousResponseId and no conversation: with store=false nothing is kept server-side, and a tool loop
            // replays its reasoning items (with their encrypted blobs) from memory instead.
            var responseOptions = new CreateResponseOptions
            {
                StoredOutputEnabled = false,
                ParallelToolCallsEnabled = parallelToolCalls
            };

            if (includeReasoning)
            {
                responseOptions.ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto
                };
                if (effort is { } level)
                {
                    responseOptions.ReasoningOptions.ReasoningEffortLevel = level;
                }

                responseOptions.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
            }

            return responseOptions;
        };
        return result;
    }
#pragma warning restore OPENAI001
}
