namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Xml.Linq;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimeStatePackagingTests
{
    private static readonly RuntimeDirectoryProtection[] RuntimeDirectoryProtections =
    [
        new("development/**", "XE-Local-AI-Engine.Client/development/"),
        new("generated-images/**", "XE-Local-AI-Engine.Client/generated-images/"),
        new("logs/**", "XE-Local-AI-Engine.Client/logs/"),
        new("backups/**", "XE-Local-AI-Engine.Client/backups/"),
        new("dp-keys/**", "dp-keys/"),
        new("models/**", "XE-Local-AI-Engine.Client/models/"),
        new("uploaded-files/**", "uploaded-files/"),
        new("agent-home-state/**", "agent-home-state/"),
        new("knowledge-base/**", "XE-Local-AI-Engine.Client/knowledge-base/")
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

    private sealed record RuntimeDirectoryProtection(string ProjectGlob, string GitIgnorePattern);
}
