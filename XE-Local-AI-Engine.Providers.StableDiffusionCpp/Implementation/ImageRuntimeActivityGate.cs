namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed class ImageRuntimeActivityGate : RuntimeActivityGate, IImageRuntimeActivityGate
{
    public ImageRuntimeActivityGate(IRuntimeResidencyChangeNotifier? residencyNotifier = null)
        : base(residencyNotifier)
    {
    }
}
