namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Bans <c>ProducesProblemFE</c> in the host: it advertises FastEndpoints' <c>ErrorResponse</c>, a body this host never
///     writes because <c>Program.cs</c> configures <c>Errors.UseProblemDetails</c>.
/// </summary>
/// <remarks>
///     A raw-text scan of the host project, comments stripped. The positive control (<c>ProducesProblemDetails</c> is
///     found) keeps the scan from passing by seeing nothing. Which declaration fits which body: wiki 09.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class EndpointErrorBodyDeclarationTests
{
    [Test]
    public void NoEndpoint_DeclaresTheFastEndpointsErrorResponse()
    {
        var directory = RepositoryPaths.Combine("XE-Local-AI-Engine.Client");
        AssertEx.True(Directory.Exists(directory), "The host project is missing, so this guard would scan nothing.");

        var offenders = new List<string>();
        var declaresProblemDetails = false;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepositoryPaths.Root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var code = SourceCommentStripper.StripComments(File.ReadAllText(file));
            declaresProblemDetails |= code.Contains(".ProducesProblemDetails(", StringComparison.Ordinal);
            if (code.Contains("ProducesProblemFE", StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        AssertEx.True(declaresProblemDetails, "No endpoint declares ProducesProblemDetails, so the scan saw nothing.");
        AssertEx.Empty(offenders,
            $"ProducesProblemFE advertises a body this host never writes; declare ProducesProblemDetails(status), or Produces(404) for Send.NotFoundAsync. Offenders: {string.Join(", ", offenders)}");
    }
}
