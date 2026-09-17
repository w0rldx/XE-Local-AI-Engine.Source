namespace XE_Local_AI_Engine.Client.Persistence.Tests.Categories;

/// <summary>The three values a class-level <c>[Category]</c> may carry; see <c>docs/wiki/17-writing-tests.md</c> §1b.</summary>
/// <remarks>
///     One copy per test project, globally imported from the project file, because no project is referenced by all
///     three and a test class must reach these constants without a using directive.
/// </remarks>
internal static class TestCategories
{
    /// <summary>Pure logic against in-memory collaborators: no host, no real database, no socket, no child process.</summary>
    internal const string Unit = "Unit";

    /// <summary>Starts something real in-process: a host, a real database, a fake server on a socket, a child process.</summary>
    internal const string Integration = "Integration";

    /// <summary>Needs infrastructure the box may not have; never part of the default gate.</summary>
    internal const string ExternalInfra = "ExternalInfra";
}
