namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

public interface ILocalChatRuntimePackageBuilder
{
    RuntimePackage Build(LocalChatRuntimePackageRequest request);
}
