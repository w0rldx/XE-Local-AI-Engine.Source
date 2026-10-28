namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Hand-written <see cref="IClientLocalToolRegistry" /> fake that resolves nothing, for a subject that consults
///     both executable registries but whose test only stages the <see cref="IAgentToolRegistry" /> half.
/// </summary>
/// <remarks>
///     The interface is internal to <c>XE-Local-AI-Engine.AI.Agent</c>, and Castle DynamicProxy cannot proxy it without
///     an <c>InternalsVisibleTo("DynamicProxyGenAssembly2")</c> grant this repo deliberately does not add (same
///     reasoning as <see cref="FakeAgentInstructionProvider" />).</remarks>
internal sealed class EmptyClientLocalToolRegistry : IClientLocalToolRegistry
{
    public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
    {
        tool = null;
        return false;
    }
}
