namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Hand-written <see cref="IClientLocalToolRegistry" /> fake that resolves nothing, for a subject that consults
///     both executable registries but whose test only stages the <see cref="IAgentToolRegistry" /> half.
///     <see cref="IClientLocalToolRegistry" /> is internal to <c>XE-Local-AI-Engine.AI.Agent</c> and Castle
///     DynamicProxy cannot proxy an internal type without an <c>InternalsVisibleTo("DynamicProxyGenAssembly2")</c>
///     grant this repo deliberately does not add, so a substitute is not an option here (see
///     <see cref="FakeAgentInstructionProvider" /> for the same reasoning).
/// </summary>
internal sealed class EmptyClientLocalToolRegistry : IClientLocalToolRegistry
{
    public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
    {
        tool = null;
        return false;
    }
}
