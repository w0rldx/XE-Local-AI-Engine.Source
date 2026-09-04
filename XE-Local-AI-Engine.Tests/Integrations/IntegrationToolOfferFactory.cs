namespace XE_Local_AI_Engine.Tests.Integrations;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     A REAL <see cref="LocalToolOfferProvider" /> for the suites that must prove what the offer does and does not
///     carry. Shared by the offer suite and the coordinator harness so both judge the same projections — a substitute
///     could be told to omit <c>emit_output</c> and would prove nothing about the class that actually omits it.
/// </summary>
internal static class IntegrationToolOfferFactory
{
    public const string CapableModel = "qwen3:8b";

    public static LocalToolOfferProvider Create(params string[] toolCapableModels) =>
        new(new FakeAgentToolRegistry([
                new LocalChatToolDescriptor(AgentHomeToolDefinition.ToolName, "Runs an agent task.", """{"type":"object"}""", RequiresApproval: true),
                new LocalChatToolDescriptor("open_url", "Opens a URL.", """{"type":"object"}""", RequiresApproval: false)
            ]),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            StubNodeRuntimeSettings.Create().WithToolCapableModels(toolCapableModels.Length == 0 ? [CapableModel] : toolCapableModels).Build(),
            NullCustomToolScopeFactory.Instance,
            new FakeModelTrustResolver(),
            allowCloudKnowledgeAccess: false);

    private sealed class FakeAgentToolRegistry(IReadOnlyList<LocalChatToolDescriptor> descriptors) : IAgentToolRegistry
    {
        public IReadOnlyList<AITool> GetLocalChatTools() => [];

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors() => descriptors;
    }
}
