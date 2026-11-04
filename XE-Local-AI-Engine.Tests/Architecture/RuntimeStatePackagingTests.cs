namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Xml.Linq;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class RuntimeStatePackagingTests
{
    private static readonly RuntimeDirectoryProtection[] RuntimeDirectoryProtections =
    [
        new()
        {
            ProjectGlob = "development/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/development/"
        },
        new()
        {
            ProjectGlob = "generated-images/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/generated-images/"
        },
        new()
        {
            ProjectGlob = "logs/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/logs/"
        },
        new()
        {
            ProjectGlob = "backups/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/backups/"
        },
        new()
        {
            ProjectGlob = "dp-keys/**",
            GitIgnorePattern = "dp-keys/"
        },
        new()
        {
            ProjectGlob = "models/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/models/"
        },
        new()
        {
            ProjectGlob = "uploaded-files/**",
            GitIgnorePattern = "uploaded-files/"
        },
        new()
        {
            ProjectGlob = "agent-home-state/**",
            GitIgnorePattern = "agent-home-state/"
        },
        new()
        {
            ProjectGlob = "knowledge-base/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/knowledge-base/"
        },
        new()
        {
            ProjectGlob = "training/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/training/"
        },
        new()
        {
            ProjectGlob = "runtime/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/runtime/"
        },
        new()
        {
            ProjectGlob = "external-apps/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/external-apps/"
        },
        new()
        {
            ProjectGlob = "model-lab/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/model-lab/"
        },
        new()
        {
            ProjectGlob = "benchmarks/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/benchmarks/"
        },
        new()
        {
            ProjectGlob = "dev-workflows/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/dev-workflows/"
        },
        new()
        {
            ProjectGlob = "work-sessions/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/work-sessions/"
        },
        new()
        {
            ProjectGlob = "dead-letter-queue/**",
            GitIgnorePattern = "XE-Local-AI-Engine.Client/dead-letter-queue/"
        },
        new()
        {
            ProjectGlob = "tmp/**",
            GitIgnorePattern = "tmp/"
        }
    ];

    private static readonly string[] WebSdkItemTypes =
    [
        "Compile",
        "Content",
        "None",
        "EmbeddedResource"
    ];

    private static readonly string CanonicalProjectRemoveValue =
        string.Join(';', RuntimeDirectoryProtections.Select(static protection => protection.ProjectGlob));

    [Test]
    public void ClientRuntimeDirectories_AreExcludedFromEveryWebSdkItemType()
    {
        var project = XDocument.Load(RepositoryPaths.ClientProject("XE-Local-AI-Engine.Client.csproj"));

        foreach (var itemType in WebSdkItemTypes)
        {
            var removeValues = project.Descendants(itemType)
                                      .Select(static item => (string?)item.Attribute("Remove"))
                                      .Where(static value => value is not null)
                                      .Select(static value => value!)
                                      .ToArray();

            AssertEx.ContainsSingle(removeValues,
                value => string.Equals(value, CanonicalProjectRemoveValue, StringComparison.Ordinal),
                $"{itemType} must remove the complete canonical runtime directory list from Web SDK item globbing.");
        }
    }

    [Test]
    public void ClientRuntimeDirectories_AreIgnoredFromSourceControl()
    {
        var ignoreEntries = File.ReadLines(RepositoryPaths.Combine(".gitignore"))
                                .Select(static line => line.Trim())
                                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                                .ToHashSet(StringComparer.Ordinal);

        foreach (var protection in RuntimeDirectoryProtections)
        {
            AssertEx.Contains(ignoreEntries,
                protection.GitIgnorePattern,
                $"Runtime directory '{protection.ProjectGlob}' must have a corresponding .gitignore entry.");
        }
    }

    /// <summary>The way this list drifted: a runtime directory gained its .gitignore line and never reached the project, so a used checkout published its contents.</summary>
    [Test]
    public void EveryGitIgnoredClientDirectory_IsARuntimeDirectoryProtection()
    {
        const string prefix = "XE-Local-AI-Engine.Client/";
        var missing = File.ReadLines(RepositoryPaths.Combine(".gitignore"))
                          .Select(static line => line.Trim())
                          .Where(static line => line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith('/'))
                          .Where(static line => !RuntimeDirectoryProtections.Any(protection => string.Equals(protection.GitIgnorePattern, line, StringComparison.Ordinal)))
                          .ToArray();

        AssertEx.Empty(missing, $"Add these to the list here and to the project's Remove lists: {string.Join(", ", missing)}");
    }

    /// <summary>The second guard: the packagers refuse an output holding any of them, whichever way one got past the project.</summary>
    [Test]
    public void EveryRuntimeDirectory_IsRefusedByTheReleasePackagerGuard()
    {
        var guard = File.ReadAllText(RepositoryPaths.Combine("publish", "package-rc.sh"));
        var missing = RuntimeDirectoryProtections
                      .Select(static protection => protection.ProjectGlob.Replace("/**", string.Empty, StringComparison.Ordinal))
                      .Where(directory => !guard.Contains($"\"{directory}|", StringComparison.Ordinal))
                      .ToArray();

        AssertEx.Empty(missing, $"assert_publish_output_clean does not name: {string.Join(", ", missing)}");
    }

    /// <summary>Off local mode the data root is the project directory, and the Web SDK's json glob takes a file a node writes there.</summary>
    [Test]
    [Arguments("node-settings.json")]
    [Arguments("model-catalog-remote-cache.json")]
    public void ClientRuntimeJsonFiles_AreRemovedFromContentAndNone_AndIgnored(string fileName)
    {
        var project = XDocument.Load(RepositoryPaths.ClientProject("XE-Local-AI-Engine.Client.csproj"));
        foreach (var itemType in new[] { "Content", "None" })
        {
            AssertEx.True(project.Descendants(itemType).Any(item => string.Equals((string?)item.Attribute("Remove"), fileName, StringComparison.Ordinal)),
                $"{itemType} must remove {fileName}.");
        }

        AssertEx.True(File.ReadLines(RepositoryPaths.Combine(".gitignore")).Any(line => string.Equals(line.Trim(), fileName, StringComparison.Ordinal)),
            $"{fileName} must have a .gitignore line.");
    }

    [Test]
    public void TheCatalogCacheFile_IsRefusedByTheReleasePackagerGuard()
    {
        AssertEx.Contains(File.ReadAllText(RepositoryPaths.Combine("publish", "package-rc.sh")), "\"model-catalog-remote-cache.json|", StringComparison.Ordinal);
    }

    [Test]
    public void PublishedRepoTreeIncludes_ExcludeEveryHiddenDirectory()
    {
        var project = XDocument.Load(RepositoryPaths.ClientProject("XE-Local-AI-Engine.Client.csproj"));

        // Every item type, Never or not: a later Update or another item type can still publish the tree.
        var repoTreeIncludes = project.Descendants()
                                      .Where(static item => item.Attribute("Include") is not null)
                                      .SelectMany(static item => ((string)item.Attribute("Include")!)
                                                                 .Split(';')
                                                                 .Where(static pattern => pattern.Contains("..", StringComparison.Ordinal)
                                                                                          && pattern.Contains("**", StringComparison.Ordinal))
                                                                 .Select(pattern => (Pattern: pattern, Excludes: ((string?)item.Attribute("Exclude") ?? string.Empty).Split(';'))))
                                      .ToArray();
        var unguarded = repoTreeIncludes
                        .Where(static include => !include.Excludes.Contains(include.Pattern.Split("**")[0] + "**/.*/**", StringComparer.Ordinal))
                        .Select(static include => include.Pattern)
                        .ToArray();

        AssertEx.NotEmpty(repoTreeIncludes, "Expected the Client project to publish at least one repo tree outside its directory.");
        AssertEx.Empty(unguarded, $"A published repo-tree include must exclude '<tree>/**/.*/**' (local hidden state ships otherwise): {string.Join(", ", unguarded)}");
    }

    private sealed record RuntimeDirectoryProtection
    {
        public required string ProjectGlob { get; init; }

        public required string GitIgnorePattern { get; init; }
    }
}
