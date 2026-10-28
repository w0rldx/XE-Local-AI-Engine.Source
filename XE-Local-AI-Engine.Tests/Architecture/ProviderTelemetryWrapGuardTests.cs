namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Every background <c>provider.CreateChatClient(...)</c> under <c>Client.Application/Services</c> must be wrapped
///     in <c>.WithProviderTelemetry()</c>, or its round emits no gen_ai span. This source scan fails on a deleted
///     suffix and on a new unwrapped site.
/// </summary>
/// <remarks>
///     The helper's own tests run it in isolation and stay green either way. The one exception is
///     <c>ModelRoutingLocalChatClient</c>, whose client <c>DecorateChatClientPipeline</c> already decorates (a wrap
///     would emit a second span per round). Whole-line comments are stripped first, since prose cites the call freely,
///     and only occurrences with a leading dot count, so an interface declaration or implementing signature is not a
///     call site.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ProviderTelemetryWrapGuardTests
{
    private const string CreateCall = ".CreateChatClient(";
    private const string TelemetryWrap = ".WithProviderTelemetry()";

    private static readonly IReadOnlySet<string> UnwrappedAllowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "CloudProviders/Implementation/ModelRoutingLocalChatClient.cs"
    };

    [Test]
    public void EveryBackgroundCreateChatClientSite_IsWrappedInProviderTelemetry()
    {
        var servicesRoot = RepositoryPaths.Combine("XE-Local-AI-Engine.Client.Application", "Services");
        AssertEx.True(Directory.Exists(servicesRoot), $"The scanned root '{servicesRoot}' must exist for this guard to mean anything.");

        var sites = Directory.EnumerateFiles(servicesRoot, "*.cs", SearchOption.AllDirectories)
                             .Select(path => new
                             {
                                 RelativePath = Path.GetRelativePath(servicesRoot, path).Replace('\\', '/'),
                                 Source = StripWholeLineComments(File.ReadAllText(path))
                             })
                             .Select(file => new
                             {
                                 file.RelativePath,
                                 Creates = Occurrences(file.Source, CreateCall),
                                 Wraps = Occurrences(file.Source, TelemetryWrap)
                             })
                             .Where(static file => file.Creates > 0 || file.Wraps > 0)
                             .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
                             .ToArray();

        // Non-vacuity: a broken glob or a moved folder would otherwise report "no offenders" as a pass.
        AssertEx.True(sites.Sum(static file => file.Creates) >= 12,
            $"Expected at least the twelve known CreateChatClient sites under Services/; the scan found {sites.Sum(static file => file.Creates)} across {sites.Length} files.");
        AssertEx.True(UnwrappedAllowlist.All(allowed => sites.Any(file => string.Equals(file.RelativePath, allowed, StringComparison.Ordinal))),
            "The allow-listed unwrapped site was not seen by the scan — the allow-list is stale or the scan is broken.");

        var offenders = sites.Where(file => !UnwrappedAllowlist.Contains(file.RelativePath) && file.Creates != file.Wraps)
                             .Select(static file => $"{file.RelativePath}: {file.Creates} CreateChatClient, {file.Wraps} WithProviderTelemetry")
                             .ToArray();

        AssertEx.Empty(offenders,
            "Every background CreateChatClient call under Client.Application/Services must carry .WithProviderTelemetry(), "
            + "or that provider round emits no gen_ai span. Wrap the site, or add it to this test's allow-list with the reason.");
    }

    private static int Occurrences(string source, string token) =>
        source.Split(token, StringSplitOptions.None).Length - 1;

    private static string StripWholeLineComments(string source)
    {
        var kept = source.Split('\n')
                         .Where(static line =>
                         {
                             var trimmed = line.TrimStart();
                             return !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith('*');
                         });

        return string.Join('\n', kept);
    }
}
