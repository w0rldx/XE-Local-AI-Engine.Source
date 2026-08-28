namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

using System.ClientModel.Primitives;

/// <summary>
///     The authentication policy for a KEYLESS OpenAI-compatible endpoint: it adds no header at all and simply passes
///     the message down the pipeline.
/// </summary>
/// <remarks>
///     It exists because "no key" and "any key" are not the same request. The OpenAI SDK's own
///     <see cref="System.ClientModel.ApiKeyCredential" /> path always writes <c>Authorization: Bearer …</c>, so a
///     sentinel value would put a bogus credential on the wire — harmless against a llama-server that ignores it, but
///     an outright 401 against any endpoint that validates the header it was sent, and a value that then shows up in
///     the remote server's access logs. Passing this policy as the SDK's FIXED authentication slot (the
///     <c>OpenAIClient(AuthenticationPolicy, OpenAIClientOptions)</c> constructor) makes it the last writer, so the
///     SDK's own placeholder-credential policy never runs and the request leaves with no <c>Authorization</c> header
///     whatsoever.
/// </remarks>
internal sealed class UnauthenticatedPipelinePolicy : AuthenticationPolicy
{
    /// <summary>The single shared instance; the policy is stateless.</summary>
    public static UnauthenticatedPipelinePolicy Instance { get; } = new();

    private UnauthenticatedPipelinePolicy()
    {
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        return ProcessNextAsync(message, pipeline, currentIndex);
    }
}
