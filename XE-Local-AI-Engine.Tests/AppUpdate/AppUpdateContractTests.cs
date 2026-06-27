namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Reflection;
using XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Static security guarantees on the app-update / GitHub-auth surface: no response DTO carries the access token or the
///     device_code, and every endpoint is Operator-gated. The token/device_code checks reflect over the
///     wire DTOs; the Operator-gating check scans each endpoint's Configure() source for the Operator policy (the same
///     source-scan technique used by CodexAuthServiceTests).
/// </summary>
public sealed class AppUpdateContractTests
{
    private static readonly Type[] ResponseContracts =
    [
        typeof(GitHubAuthStartResponse),
        typeof(GitHubAuthPollResponse),
        typeof(GitHubAuthStatusResponse),
        typeof(AppUpdateStatusResponse),
        typeof(ApplyAppUpdateResponse)
    ];

    private static readonly string[] EndpointSourceFiles =
    [
        "StartGitHubAuthEndpoint.cs",
        "PollGitHubAuthEndpoint.cs",
        "GetGitHubAuthStatusEndpoint.cs",
        "SignOutGitHubAuthEndpoint.cs",
        "GetAppUpdateStatusEndpoint.cs",
        "ApplyAppUpdateEndpoint.cs"
    ];

    [Test]
    public void AppUpdateContracts_ContainNoTokenField()
    {
        foreach (var contract in ResponseContracts)
        {
            foreach (var property in contract.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = property.Name;
                AssertEx.False(name.Contains("Token", StringComparison.OrdinalIgnoreCase),
                    $"{contract.Name}.{name} must not expose token material");
                AssertEx.False(name.Contains("DeviceCode", StringComparison.OrdinalIgnoreCase),
                    $"{contract.Name}.{name} must not expose the device_code");
                AssertEx.False(name.Contains("AccessToken", StringComparison.OrdinalIgnoreCase),
                    $"{contract.Name}.{name} must not expose the access token");
            }
        }
    }

    [Test]
    public void GitHubAuthStartResponse_DoesNotReturnDeviceCode()
    {
        // The start contract returns only the user-facing code + verification URI; the secret device_code stays server-side.
        var propertyNames = typeof(GitHubAuthStartResponse)
                            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Select(p => p.Name)
                            .ToArray();

        AssertEx.Contains(string.Join(",", propertyNames), "UserCode");
        AssertEx.Contains(string.Join(",", propertyNames), "VerificationUri");
        AssertEx.False(propertyNames.Any(n => n.Contains("DeviceCode", StringComparison.OrdinalIgnoreCase)),
            "the start response must never carry the device_code");
    }

    [Test]
    public async Task AllUpdateEndpoints_AreOperatorGated()
    {
        foreach (var fileName in EndpointSourceFiles)
        {
            var source = await File.ReadAllTextAsync(GetEndpointPath(fileName));
            AssertEx.True(source.Contains("Policies(NodeAuthorizationPolicies.Operator)", StringComparison.Ordinal),
                $"{fileName} must gate on the Operator policy");
        }
    }

    private static string GetEndpointPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName,
                "XE-Local-AI-Engine.Client",
                "Endpoints",
                "AppUpdate",
                "V1",
                fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate endpoint source {fileName}.");
    }
}
