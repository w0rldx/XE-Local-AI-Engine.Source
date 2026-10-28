namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Hand-written <see cref="IToolRelevanceCoreSet" /> fake returning a fixed name set (empty by default). The real
///     one composes the node tool catalog, which a runner test should not stand up; with relevance off (the shipped
///     default) it is never read.
/// </summary>
internal sealed class FakeToolRelevanceCoreSet : IToolRelevanceCoreSet
{
    private readonly IReadOnlySet<string> _coreNames;

    public FakeToolRelevanceCoreSet(params string[] coreNames)
    {
        _coreNames = new HashSet<string>(coreNames, StringComparer.Ordinal);
    }

    public IReadOnlySet<string> GetCoreToolNames()
    {
        return _coreNames;
    }
}
