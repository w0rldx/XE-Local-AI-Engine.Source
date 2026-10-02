namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Keeps <c>IModelTrustResolver</c> the single trust authority: a policy gate that asks <c>IsCodexModel</c> itself, or
///     grows its own trust-boundary helper, is a second locality answer that drifts from the first.
/// </summary>
/// <remarks>
///     A raw-text scan of the host and application projects, comments stripped, like <c>ThirdPartySdkBoundaryTests</c>.
///     The allowed files are the routing rule (the cloud factory), the trust resolver, the capability matrix pick and
///     the deployment-name reservation; each must still reference the call, so the scan cannot pass by seeing nothing.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ModelTrustAuthorityGuardTests
{
    private const string CodexCheck = "CodexModelCatalog.IsCodexModel(";
    private const string TrustBoundaryHelper = "IsOutsideTrustBoundary";

    private static readonly string[] ScannedProjects = ["XE-Local-AI-Engine.Client", "XE-Local-AI-Engine.Client.Application"];

    private static readonly string[] CodexCheckAllowlist =
    [
        "XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/ModelCapabilityResolver.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/CloudSettingsPolicy.cs",
        "XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Implementation/ActiveCloudChatClientFactory.cs",
        "XE-Local-AI-Engine.Client.Application/Services/ExternalProviders/Implementation/ModelTrustResolver.cs"
    ];

    [Test]
    public void OnlyTheTrustAuthorityAndTheRoutingRule_AskIsCodexModel()
    {
        var offenders = FilesContaining(CodexCheck);

        AssertEx.Equal(string.Join('\n', CodexCheckAllowlist),
            string.Join('\n', offenders),
            $"'{CodexCheck}' outside the allowlist is a second trust answer: ask IModelTrustResolver instead. "
            + "An allowlisted file that no longer references it is removed from the list.");
    }

    [Test]
    public void NoFile_DeclaresItsOwnTrustBoundaryHelper()
    {
        var offenders = FilesContaining(TrustBoundaryHelper);

        AssertEx.Empty(offenders, $"A '{TrustBoundaryHelper}' helper re-derives locality: delegate to IModelTrustResolver. Offenders: {string.Join(", ", offenders)}");
    }

    private static List<string> FilesContaining(string symbol)
    {
        var offenders = new List<string>();
        foreach (var project in ScannedProjects)
        {
            var directory = RepositoryPaths.Combine(project);
            AssertEx.True(Directory.Exists(directory), $"'{project}' is missing, so this guard would scan nothing.");

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepositoryPaths.Root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (SourceCommentStripper.StripComments(File.ReadAllText(file)).Contains(symbol, StringComparison.Ordinal))
                {
                    offenders.Add(relative);
                }
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }
}
