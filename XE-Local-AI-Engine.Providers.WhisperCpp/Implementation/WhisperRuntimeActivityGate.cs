namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

public sealed class WhisperRuntimeActivityGate : RuntimeActivityGate, IWhisperRuntimeActivityGate
{
    public WhisperRuntimeActivityGate(IRuntimeResidencyChangeNotifier? residencyNotifier = null)
        : base(residencyNotifier)
    {
    }
}
