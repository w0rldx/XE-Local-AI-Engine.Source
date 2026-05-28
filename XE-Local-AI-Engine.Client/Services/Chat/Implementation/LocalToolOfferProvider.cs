namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Chat;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;

internal sealed class LocalToolOfferProvider : ILocalToolOfferProvider
{
    private readonly IReadOnlyList<AllowedToolDto> _offeredTools;

    public LocalToolOfferProvider(IAgentToolRegistry toolRegistry)
    {
        ArgumentNullException.ThrowIfNull(toolRegistry);

        // The catalog is static for the process lifetime, so map it once. Each tool's Id is derived deterministically
        // from its name so the offer list is byte-identical across sends (the config hash ignores the Id, but a
        // stable Id keeps client-side rendering and equality predictable).
        _offeredTools =
        [
            .. toolRegistry.GetLocalChatToolDescriptors()
                           .Select(static descriptor => new AllowedToolDto
                           {
                               Id = DeriveDeterministicId(descriptor.Name),
                               Name = descriptor.Name,
                               Location = ToolLocation.ClientLocal,
                               ParameterSchema = descriptor.ParameterSchema,
                               RequiresApproval = descriptor.RequiresApproval
                           })
        ];
    }

    public IReadOnlyList<AllowedToolDto> GetOfferedTools()
    {
        return _offeredTools;
    }

    private static Guid DeriveDeterministicId(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"local-tool:{name}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
