namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>Guards the migrated SQLite template in <see cref="TestServerWebAppFactory" />: where it is published and the stale sweep.</summary>
[Category(TestCategories.Integration)]
public sealed class TestServerWebAppFactoryTemplateSweepTests
{
    // scripts/run-tests-memory-safe.sh runs exactly this test, by name, to pre-warm the template in the base output tree
    // before it clones the per-slot coverage trees. Renaming it breaks that pre-warm loudly (zero-match filter), not silently.
    [Test]
    public void EnsureMigratedTemplate_PublishesTheTemplateUnderThisBuildsOutputDirectory()
    {
        var template = TestServerWebAppFactory.EnsureMigratedTemplate();

        AssertEx.True(File.Exists(template), $"The migrated template must exist after it was built: {template}");
        AssertEx.Equal(Path.Combine(AppContext.BaseDirectory, "sqlite-templates"),
            Path.GetDirectoryName(template),
            "The template must live under this build's output directory, where a copied output tree carries it along.");
    }

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
