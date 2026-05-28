namespace XE_Local_AI_Engine.AI.Agent.Instructions.Implementation;

using System.Reflection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Instructions;

internal sealed class AgentInstructionProvider : IAgentInstructionProvider
{
    private readonly IOptions<LocalChatAgentOptions> _localChatOptions;

    public AgentInstructionProvider(IOptions<LocalChatAgentOptions> localChatOptions)
    {
        _localChatOptions = localChatOptions ?? throw new ArgumentNullException(nameof(localChatOptions));
    }

    public string GetLocalChatInstructions()
    {
        var resourceName = _localChatOptions.Value.InstructionsResource;
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded instructions resource '{resourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
