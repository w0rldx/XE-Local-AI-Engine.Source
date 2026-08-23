namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Xml.Linq;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimeLicensePackagingTests
{
    [Test]
    public void ReleaseVersionManifest_PinsTheSupportedRuntimeAndIsImportedByTheBuild()
    {
        var releaseVersionPath = RepositoryPaths.Combine("eng", "ReleaseVersion.props");
        var releaseVersion = XDocument.Load(releaseVersionPath);
        var buildProps = XDocument.Load(RepositoryPaths.Combine("Directory.Build.props"));

        AssertEx.Equal("10.0.11", Property(releaseVersion, "DotNetRuntimeVersion"));
        AssertEx.NotEmpty(Property(releaseVersion, "VersionPrefix"));
        AssertEx.True(buildProps.Descendants("Import").Any(import =>
            string.Equals((string?)import.Attribute("Project"), "eng/ReleaseVersion.props", StringComparison.Ordinal)));
    }

    [Test]
    public void PublishProfiles_KeepLinuxSelfContainedButMakeWindowsFrameworkDependent()
    {
        var windows = XDocument.Load(RepositoryPaths.ClientProject("Properties", "PublishProfiles", "win-x64.pubxml"));
        AssertEx.Equal("false", Property(windows, "SelfContained"));
        AssertEx.Equal("false", Property(windows, "PublishSingleFile"));
        AssertEx.Equal("false", Property(windows, "UseAppHost"));
        AssertEx.Equal("LatestPatch", Property(windows, "RollForward"));
        AssertEx.Equal("$(DotNetRuntimeVersion)", Property(windows, "RuntimeFrameworkVersion"));

        var windowsLauncher = XDocument.Load(RepositoryPaths.Combine("XE-Local-AI-Engine.WindowsLauncher", "Properties", "PublishProfiles", "win-x64.pubxml"));
        AssertEx.Equal("false", Property(windowsLauncher, "SelfContained"));
        AssertEx.Equal("false", Property(windowsLauncher, "PublishSingleFile"));
        AssertEx.Equal("true", Property(windowsLauncher, "UseAppHost"));
        AssertEx.Equal("LatestPatch", Property(windowsLauncher, "RollForward"));
        AssertEx.Equal("$(DotNetRuntimeVersion)", Property(windowsLauncher, "RuntimeFrameworkVersion"));

        var linux = XDocument.Load(RepositoryPaths.ClientProject("Properties", "PublishProfiles", "linux-x64.pubxml"));
        AssertEx.Equal("true", Property(linux, "SelfContained"));
        AssertEx.Equal("true", Property(linux, "PublishSingleFile"));
        AssertEx.Equal("$(DotNetRuntimeVersion)", Property(linux, "RuntimeFrameworkVersion"));
    }

    [Test]
    public void ClientPublish_CarriesThePerRidDotNetLicenseAndThirdPartyNotices()
    {
        var project = XDocument.Load(RepositoryPaths.ClientProject("XE-Local-AI-Engine.Client.csproj"));
        var runtimeTarget = project.Descendants("Target")
                                   .Single(target => string.Equals((string?)target.Attribute("Name"),
                                       "ValidateDotNetRuntimeLicenseAssets",
                                       StringComparison.Ordinal));
        AssertEx.Contains((string?)runtimeTarget.Attribute("Condition") ?? string.Empty,
            "'$(SelfContained)' == 'true'");
        var linkedPublishFiles = project.Descendants("ResolvedFileToPublish")
                                        .Select(static item => (string?)item.Element("RelativePath"))
                                        .Where(static path => path is not null)
                                        .Select(static path => path!)
                                        .ToHashSet(StringComparer.Ordinal);

        AssertEx.Contains(linkedPublishFiles, "licenses/dotnet/DOTNET-RUNTIME-LICENSE.txt");
        AssertEx.Contains(linkedPublishFiles, "licenses/dotnet/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt");
        AssertEx.Contains(linkedPublishFiles, "licenses/dotnet/ASPNETCORE-RUNTIME-LICENSE.txt");
        AssertEx.Contains(linkedPublishFiles, "licenses/dotnet/ASPNETCORE-RUNTIME-THIRD-PARTY-NOTICES.txt");
        AssertEx.Contains(linkedPublishFiles, "wwwroot/licenses/dotnet/DOTNET-RUNTIME-LICENSE.txt");
        AssertEx.Contains(linkedPublishFiles, "wwwroot/licenses/dotnet/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt");
        AssertEx.Contains(linkedPublishFiles, "wwwroot/licenses/dotnet/ASPNETCORE-RUNTIME-LICENSE.txt");
        AssertEx.Contains(linkedPublishFiles, "wwwroot/licenses/dotnet/ASPNETCORE-RUNTIME-THIRD-PARTY-NOTICES.txt");
        AssertEx.False(linkedPublishFiles.Contains("licenses/dotnet/DOTNET-LIBRARY-LICENSE.html"));
        AssertEx.False(linkedPublishFiles.Contains("wwwroot/licenses/dotnet/DOTNET-LIBRARY-LICENSE.html"));

        var curatedLicenseLinks = project.Descendants("Content")
                                         .Select(static content => (string?)content.Attribute("Link"))
                                         .Where(static link => link is not null)
                                         .Select(static link => link!)
                                         .ToArray();
        AssertEx.Contains(curatedLicenseLinks,
            "licenses/nuget/%(RecursiveDir)%(Filename)%(Extension)");
    }

    [Test]
    public void WindowsLauncherPublish_CarriesTheApphostMitLicenseAndNotices()
    {
        var project = XDocument.Load(RepositoryPaths.Combine("XE-Local-AI-Engine.WindowsLauncher", "XE-Local-AI-Engine.WindowsLauncher.csproj"));
        var linkedPublishFiles = project.Descendants("ResolvedFileToPublish")
                                        .Select(static item => (string?)item.Element("RelativePath"))
                                        .Where(static path => path is not null)
                                        .Select(static path => path!)
                                        .ToHashSet(StringComparer.Ordinal);

        AssertEx.Contains(linkedPublishFiles, "licenses/dotnet/DOTNET-APPHOST-LICENSE.txt");
        AssertEx.Contains(linkedPublishFiles, "licenses/dotnet/DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt");
        AssertEx.False(linkedPublishFiles.Contains("licenses/dotnet/DOTNET-LIBRARY-LICENSE.html"));
    }

    [Test]
    public void ReleaseProjects_DoNotReferenceTheRemovedPreviewDevUiPackages()
    {
        var projects = new[]
        {
            RepositoryPaths.ClientProject("XE-Local-AI-Engine.Client.csproj"),
            RepositoryPaths.Combine("XE-Local-AI-Engine.AI.Agent", "XE-Local-AI-Engine.AI.Agent.csproj")
        };
        var forbidden = new[]
        {
            "Microsoft.Agents.AI.DevUI",
            "Microsoft.Agents.AI.Hosting",
            "Microsoft.Agents.AI.Hosting.OpenAI"
        };

        foreach (var projectPath in projects)
        {
            var references = XDocument.Load(projectPath)
                                      .Descendants("PackageReference")
                                      .Select(static reference => (string?)reference.Attribute("Include"))
                                      .Where(static name => name is not null)
                                      .Select(static name => name!)
                                      .ToHashSet(StringComparer.Ordinal);
            foreach (var package in forbidden)
            {
                AssertEx.False(references.Contains(package), $"{projectPath} must not reference {package}.");
            }
        }
    }

    private static string Property(XDocument document, string name) =>
        document.Descendants(name).Single().Value.Trim();
}
