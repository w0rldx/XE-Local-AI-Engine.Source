namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>Guards the stale-template sweep in <see cref="TestServerWebAppFactory" />.</summary>
[Category(TestCategories.Integration)]
public sealed class TestServerWebAppFactoryTemplateSweepTests
{
    [Test]
    public void SweepStaleTemplates_DeletesAnotherBuildsTemplateAndKeepsTheCurrentOne()
    {
        using var directory = new TempDirectory("xe-local-ai-engine-tests-sweep");
        var current = directory.FilePath("current-key.sqlite");
        var stale = directory.FilePath("stale-key.sqlite");
        var scratch = directory.FilePath("build-in-flight");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(current, "current");
        File.WriteAllText(stale, "stale");

        TestServerWebAppFactory.SweepStaleTemplates(directory.Path, "current-key");

        AssertEx.False(File.Exists(stale), "A template keyed on another build's module version id must be swept.");
        AssertEx.True(File.Exists(current), "The current build's template must survive the sweep.");
        AssertEx.True(Directory.Exists(scratch), "A concurrent same-build process's scratch directory must survive the sweep.");
    }
}
