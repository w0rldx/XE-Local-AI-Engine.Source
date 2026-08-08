namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Reflection;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Locks the credential-free public updater contract and endpoint authorization.</summary>
public sealed class AppUpdateContractTests
{
    private static readonly Type[] ResponseContracts = [typeof(AppUpdateStatusResponse), typeof(ApplyAppUpdateResponse)];

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
                     "ApplyAppUpdateEndpoint.cs"
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
