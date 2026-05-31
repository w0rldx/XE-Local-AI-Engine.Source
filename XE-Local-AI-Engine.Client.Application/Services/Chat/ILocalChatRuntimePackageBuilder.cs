namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Abstraction for local chat runtime package builder behavior.
/// </summary>
public interface ILocalChatRuntimePackageBuilder
{
    RuntimePackage Build(LocalChatRuntimePackageRequest request);
}
