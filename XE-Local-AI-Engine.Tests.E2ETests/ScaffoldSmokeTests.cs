namespace XE_Local_AI_Engine.Tests.E2ETests;

/// <summary>
/// Placeholder so the freshly scaffolded project compiles and the test platform
/// discovers at least one test. Real harness tests land in later steps.
/// </summary>
public sealed class ScaffoldSmokeTests
{
    [Test]
    public async Task Project_Compiles_And_Discovers()
    {
        var projectName = typeof(ScaffoldSmokeTests).Assembly.GetName().Name;
        await Assert.That(projectName).IsEqualTo("XE-Local-AI-Engine.Tests.E2ETests");
    }
}
