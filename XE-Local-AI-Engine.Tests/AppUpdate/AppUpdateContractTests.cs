namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Reflection;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Locks the credential-free public updater contract and endpoint authorization.</summary>
[Category(TestCategories.Unit)]
public sealed class AppUpdateContractTests
{
    /// <summary>Every project that references Velopack, so the downgrade guard cannot be escaped by moving the call.</summary>
    private static readonly string[] VelopackFacingProjects =
        ["XE-Local-AI-Engine.Client", "XE-Local-AI-Engine.Desktop", "XE-Local-AI-Engine.WindowsLauncher"];

    private static readonly Type[] ResponseContracts =
        [typeof(AppUpdateStatusResponse), typeof(ApplyAppUpdateResponse), typeof(SetAppUpdateChannelRequest)];

    [Test]
    public void PublicAppUpdateContracts_ContainNoAuthenticationFields()
    {
        foreach (var contract in ResponseContracts)
        {
            foreach (var property in contract.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                AssertEx.False(property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
                AssertEx.False(property.Name.Contains("Auth", StringComparison.OrdinalIgnoreCase));
                AssertEx.False(property.Name.Contains("Login", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Test]
    public void AppUpdateStatus_UsesDistinctSanitizedCheckStatus()
    {
        var properties = typeof(AppUpdateStatusResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        AssertEx.True(properties.Any(property => property.Name == nameof(AppUpdateStatusResponse.CheckStatus)));
        AssertEx.False(properties.Any(property => property.Name == "IsOffline"));
    }

    [Test]
    public async Task PublicAppUpdateEndpoints_AreOperatorGated()
    {
        foreach (var fileName in new[]
                 {
                     "GetAppUpdateStatusEndpoint.cs",
                     "ApplyAppUpdateEndpoint.cs",
                     "SetAppUpdateChannelEndpoint.cs"
                 })
        {
            var source = await File.ReadAllTextAsync(GetEndpointPath(fileName));
            AssertEx.True(source.Contains("Policies(NodeAuthorizationPolicies.Operator)", StringComparison.Ordinal));
        }
    }

    [Test]
    public void ForcedRefreshAndStartupCheck_UseAnonymousSafeCadence()
    {
        AssertEx.True(GetAppUpdateStatusEndpoint.MinRefreshInterval >= TimeSpan.FromMinutes(10));
        AssertEx.True(AppUpdateCheckService.DefaultStartupDelay >= TimeSpan.FromMinutes(10));
    }

    [Test]
    public void AppUpdateStatus_ReportsEveryChannelTheOperatorMaySelect()
    {
        var response = GetAppUpdateStatusEndpoint.ToResponse(AppUpdateSnapshot.Empty);

        AssertEx.Equal("stable,preview,development", string.Join(',', response.AvailableChannels));
        foreach (var channel in response.AvailableChannels)
        {
            AssertEx.True(AppUpdateChannelNames.TryParse(channel, out _), $"'{channel}' is not a known literal.");
        }
    }

    [Test]
    public async Task AppUpdateNeverEnablesAVersionDowngrade()
    {
        // UpdateOptions.AllowVersionDowngrade defaults to false, and that default is the ONLY thing making a channel
        // switch forward-only. D6 says "never true anywhere", so every project that can reach Velopack is scanned.
        var sources = VelopackFacingProjects
                      .Select(project => RepositoryPaths.Combine(project))
                      .SelectMany(project => Directory.GetFiles(project, "*.cs", SearchOption.AllDirectories))
                      .Where(file => !IsBuildOutput(file))
                      .ToArray();

        AssertEx.NotEmpty(sources);
        foreach (var file in sources)
        {
            var source = await File.ReadAllTextAsync(file);
            AssertEx.False(source.Contains("AllowVersionDowngrade", StringComparison.Ordinal),
                $"'{Path.GetFileName(file)}' names AllowVersionDowngrade.");
        }
    }

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
               || file.Contains($"{separator}bin{separator}", StringComparison.Ordinal);
    }

    private static string GetEndpointPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "XE-Local-AI-Engine.Client", "Endpoints", "AppUpdate", "V1", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate endpoint source {fileName}.");
    }
}
