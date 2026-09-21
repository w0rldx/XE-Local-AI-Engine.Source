namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     A resolved, ready <c>sd-server</c> daemon endpoint for one image model: the model it serves and the loopback base
///     address the job client posts jobs to.
/// </summary>
/// <remarks>
///     The base address is the server root, because sd.cpp routes are absolute <c>/sdcpp/v1/…</c>, unlike llama's
///     <c>/v1</c>-prefixed base.
/// </remarks>
public sealed class ImageServerEndpoint
{
    public required string ModelName { get; init; }

    public required Uri BaseAddress { get; init; }
}
