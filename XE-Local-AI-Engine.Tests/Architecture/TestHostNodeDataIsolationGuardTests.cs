namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A test host that overrides <c>NodeData:Directory</c> registers <c>FakeNodeDataDirectory</c>, or the product
///     <c>NodeDataDirectory</c> moves the content root's node state into the test's temp dir.
/// </summary>
/// <remarks>
///     Most hosts use the real Client directory as content root, the data root of a dev or lab host from the same
///     checkout. A raw-text scan of every test project, comments stripped, at file granularity: a file that starts a
///     host and sets the key must name the fake. A WebApplicationFactory host also sets it with UseSetting: its
///     app-configuration callbacks apply at Build(), after ConfigureServices roots dp-keys. Patterns are regexes so the
///     category scan does not read this Unit class as a host.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class TestHostNodeDataIsolationGuardTests
{
    private static readonly Regex HostStart = new(@"\bCreateAppAsync\s*\(|\bWebApplication[F]actory<", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex FactoryHost = new(@"\bWebApplication[F]actory<", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex HostSettingOverride = new(@"\bUseSetting\(\s*(?:""NodeData:Directory""|NodeDataDirectory\.ConfigurationKey)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly string[] DataDirectoryOverrides = ["\"NodeData:Directory\"", "NodeDataDirectory.ConfigurationKey"];

    [Test]
    public void EveryTestHostThatOverridesTheNodeDataDirectory_RegistersTheFake()
    {
        var hosts = new List<string>();
        var offenders = new List<string>();
        var lateOverrides = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(RepositoryPaths.Root, "XE-Local-AI-Engine.*Tests*"))
        {
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepositoryPaths.Root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                var source = SourceCommentStripper.StripComments(File.ReadAllText(file));
                if (!HostStart.IsMatch(source) || !DataDirectoryOverrides.Any(source.Contains))
                {
                    continue;
                }

                hosts.Add(relative);
                if (!source.Contains("FakeNodeDataDirectory", StringComparison.Ordinal))
                {
                    offenders.Add(relative);
                }

                if (FactoryHost.IsMatch(source) && !HostSettingOverride.IsMatch(source))
                {
                    lateOverrides.Add(relative);
                }
            }
        }

        // Non-vacuity: five such hosts exist across Tests, Cli.Tests and Tests.E2ETests; fewer means the walk is broken.
        AssertEx.True(hosts.Count >= 5, $"Expected at least five test hosts that set NodeData:Directory; found: {string.Join(", ", hosts)}");
        AssertEx.Empty(offenders,
            "These test hosts set NodeData:Directory without registering FakeNodeDataDirectory, so the product NodeDataDirectory "
            + $"moves the content root's node state into their temp dir: {string.Join(", ", offenders)}");
        AssertEx.Empty(lateOverrides,
            "These factory-built hosts set NodeData:Directory without UseSetting, so Data Protection roots dp-keys in the "
            + $"content root before the override applies: {string.Join(", ", lateOverrides)}");
    }
}
