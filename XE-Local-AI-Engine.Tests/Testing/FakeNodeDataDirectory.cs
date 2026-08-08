namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Test double for <see cref="INodeDataDirectory" />: returns a caller-supplied root so a store under test reads and
///     writes inside a temp directory instead of the real per-user data dir / content root.
/// </summary>
internal sealed class FakeNodeDataDirectory(string root) : INodeDataDirectory
{
    public string Root { get; } = root;
}
