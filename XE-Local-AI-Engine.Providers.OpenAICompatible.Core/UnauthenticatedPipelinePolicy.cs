namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

using System.ClientModel.Primitives;

/// <summary>
///     The authentication policy for a KEYLESS OpenAI-compatible endpoint: it adds no header at all and simply passes
///     the message down the pipeline.
/// </summary>
/// <remarks>
///     "No key" and "any key" are not the same request. The SDK's own
///     <see cref="System.ClientModel.ApiKeyCredential" /> path always writes <c>Authorization: Bearer …</c>, so a
///     sentinel would put a bogus credential on the wire: harmless against a llama-server that ignores it, but a 401
///     against an endpoint that validates the header, and a value landing in that server's access logs. Passed as the
///     SDK's FIXED authentication slot it is the last writer, so no <c>Authorization</c> header leaves at all.
/// </remarks>
internal sealed class UnauthenticatedPipelinePolicy : AuthenticationPolicy
{
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
