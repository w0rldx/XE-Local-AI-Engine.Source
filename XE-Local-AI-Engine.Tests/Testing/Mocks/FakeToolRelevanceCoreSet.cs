namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Hand-written <see cref="IToolRelevanceCoreSet" /> fake returning a fixed name set (empty by default). The
///     production implementation composes the node tool catalog, which a runner test has no business standing up; and
///     with the relevance feature off — the shipped default, and what every byte-identical-offer assertion depends on —
///     the set is never read at all.
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
