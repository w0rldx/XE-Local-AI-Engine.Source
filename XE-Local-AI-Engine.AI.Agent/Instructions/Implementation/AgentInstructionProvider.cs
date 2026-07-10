namespace XE_Local_AI_Engine.AI.Agent.Instructions.Implementation;

using System.Reflection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;

internal sealed class AgentInstructionProvider : IAgentInstructionProvider
{
    // App-owned, not tenant-configurable (unlike LocalChatAgentOptions.InstructionsResource): the scaffold is the
    // same boilerplate for every node, so it is not surfaced as a settable option.
    private const string BaseScaffoldResourceName = "XE_Local_AI_Engine.AI.Agent.Instructions.BaseScaffold.txt";

    private readonly IOptions<LocalChatAgentOptions> _localChatOptions;

    public AgentInstructionProvider(IOptions<LocalChatAgentOptions> localChatOptions)
    {
        _localChatOptions = localChatOptions ?? throw new ArgumentNullException(nameof(localChatOptions));
    }

    public string GetLocalChatInstructions()
    {
        return LoadEmbeddedResource(_localChatOptions.Value.InstructionsResource);
    }

    public string GetBaseScaffold()
    {
        return LoadEmbeddedResource(BaseScaffoldResourceName);
    }

    public string GetDefaultChatSystemPrompt()
    {
        return BaseInstructionComposer.Compose(GetBaseScaffold(), GetLocalChatInstructions());
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
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
