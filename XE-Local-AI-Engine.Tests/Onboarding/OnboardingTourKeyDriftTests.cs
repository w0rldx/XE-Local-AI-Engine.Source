namespace XE_Local_AI_Engine.Tests.Onboarding;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Guards the three tutorial registry keys against the default browser fixture. A drift would make an optional
///     invitation appear over unrelated browser tests and would stop the fixture from representing a returning user.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class OnboardingTourKeyDriftTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments("quick-start", "MainAppTourKey")]
    [Arguments("agents-basics", "AgentsTourKey")]
    [Arguments("knowledge-base-basics", "KnowledgeBaseTourKey")]
    public async Task ReactTutorialRegistryKey_MatchesTheE2EFixture(string tutorialId, string factoryConstant)
    {
        var registrySource = await File.ReadAllTextAsync(RepositoryPaths.Combine("XE-Local-AI-Engine.Client.React",
            "src", "core", "onboarding", "data", "TutorialRegistry.ts"));
        var factorySource = await File.ReadAllTextAsync(RepositoryPaths.Combine("XE-Local-AI-Engine.Tests.E2ETests",
            "Infrastructure", "XENodeE2EWebApplicationFactory.cs"));

        var registryPattern = "id:\\s*\\\"" + Regex.Escape(tutorialId)
                                            + "\\\"[\\s\\S]*?persistenceKey:\\s*\\\"(?<value>[^\\\"]+)\\\"";
        var factoryPattern = "public const string " + Regex.Escape(factoryConstant)
                                                    + "\\s*=\\s*\\\"(?<value>[^\\\"]+)\\\"\\s*;";

        var registryKey = Capture(registrySource, registryPattern, $"registry entry {tutorialId}");
        var fixtureKey = Capture(factorySource, factoryPattern, $"fixture constant {factoryConstant}");
        AssertEx.Equal(registryKey, fixtureKey, $"Tutorial key drifted for {tutorialId}.");
    }

    [Test]
    public async Task ReactProgressStoragePrefix_MatchesTheE2EAssertionKey()
    {
        var reactSource = await File.ReadAllTextAsync(RepositoryPaths.Combine("XE-Local-AI-Engine.Client.React",
            "src", "core", "onboarding", "hooks", "useTourState.ts"));
        var factorySource = await File.ReadAllTextAsync(RepositoryPaths.Combine("XE-Local-AI-Engine.Tests.E2ETests",
            "Infrastructure", "XENodeE2EWebApplicationFactory.cs"));

        AssertEx.Contains(reactSource, "`xe-onboarding-${persistenceKey}-step`", StringComparison.Ordinal,
            "React progress storage prefix changed");
        AssertEx.Contains(factorySource, "$\"xe-onboarding-{MainAppTourKey}-step\"", StringComparison.Ordinal,
            "E2E progress storage prefix changed");
    }

    private static string Capture(string source, string pattern, string description)
    {
        var match = Regex.Match(source, pattern, RegexOptions.None, RegexTimeout);
        AssertEx.True(match.Success, $"could not find {description}");
        return match.Groups["value"].Value;
    }
}
