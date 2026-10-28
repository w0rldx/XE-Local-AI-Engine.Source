namespace XE_Local_AI_Engine.Client.Testing.Categories;

/// <summary>The three values a class-level <c>[Category]</c> may carry; see <c>docs/wiki/17-writing-tests.md</c> §1b.</summary>
/// <remarks>
///     Globally imported from each test project file, so a test class reaches these constants without a using
///     directive. A project without a reference to this assembly links this file instead.
/// </remarks>
public static class TestCategories
{
    /// <summary>Pure logic against in-memory collaborators: no host, no real database, no socket, no child process.</summary>
    public const string Unit = "Unit";

    /// <summary>Starts something real in-process: a host, a real database, a fake server on a socket, a child process.</summary>
    public const string Integration = "Integration";

    /// <summary>Needs infrastructure the host may not have; never part of the default gate.</summary>
    public const string ExternalInfra = "ExternalInfra";
}
