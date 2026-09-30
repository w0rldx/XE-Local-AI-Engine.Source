namespace XE_Local_AI_Engine.Client.Testing.Fakes;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Test double for <see cref="INodeDataDirectory" />: returns a caller-supplied root so a store under test reads and
///     writes inside a temp directory instead of the real per-user data dir / content root.
/// </summary>
public sealed class FakeNodeDataDirectory : INodeDataDirectory
{
    public FakeNodeDataDirectory(string root)
    {
        Root = root;
    }

    public string Root { get; }
}
