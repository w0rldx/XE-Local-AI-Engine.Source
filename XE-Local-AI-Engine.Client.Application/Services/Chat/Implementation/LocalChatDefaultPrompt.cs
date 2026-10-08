namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.AI.Agent.Configuration;

/// <summary>
///     The system prompt a chat turn falls back to when no agent definition resolved: the embedded chat persona with the
///     same versioned base scaffold a resolved agent gets, so an unbound turn is covered identically to a bound one.
/// </summary>
/// <remarks>
///     Shared by the send, regenerate and pre-send context-estimate paths so the estimate measures the prompt a turn
///     actually sends.
/// </remarks>
internal static class LocalChatDefaultPrompt
{
    // The same resource AgentInstructionProvider.GetBaseScaffold reads, kept as a literal to avoid a DI dependency on
    // IAgentInstructionProvider in the chat services' already-large constructors.
    private const string BaseScaffoldResourceName = "XE_Local_AI_Engine.AI.Agent.Instructions.BaseScaffold.txt";

    /// <summary>Reads the embedded chat prompt and prepends the base scaffold.</summary>
    public static async Task<string> LoadAsync(LocalChatAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.InstructionsResource))
        {
            throw new ArgumentException("Instructions resource must be provided.", nameof(options));
        }

        var persona = await LoadEmbeddedResourceAsync(options.InstructionsResource);
        var scaffold = await LoadEmbeddedResourceAsync(BaseScaffoldResourceName);
        return string.IsNullOrWhiteSpace(scaffold) ? persona : $"{scaffold.TrimEnd()}\n\n{persona}";
    }

    // Reads an embedded manifest resource: the bytes are already in the loaded assembly image, so there is no I/O to
    // abandon. CancellationToken.None is the analyzers' documented "intentionally not propagating" opt-out.
    private static async Task<string> LoadEmbeddedResourceAsync(string resourceName)
    {
        var assembly = typeof(LocalChatAgentOptions).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
                                 ?? throw new InvalidOperationException($"Embedded instructions resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }
}
