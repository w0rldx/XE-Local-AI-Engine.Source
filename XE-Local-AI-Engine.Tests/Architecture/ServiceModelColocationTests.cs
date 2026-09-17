namespace XE_Local_AI_Engine.Tests.Architecture;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the file-level half of the placement convention that <see cref="PlacementConventionTests" /> cannot state:
///     a service implementation file declares the service, and nothing else. Contract types — the records and enums the
///     service hands across its boundary — live in a sibling <c>*Models</c> / <c>*ServiceModels</c> / <c>*Contracts</c>
///     / <c>*Dtos</c> file, where a caller can find them without opening the implementation.
///     <para>
///         ArchUnitNET cannot express this. It reflects compiled IL, which records a type's namespace and assembly but
///         not the source file it came from, and file co-location is the entire substance of the rule. A
///         namespace-level approximation would flag ~250 files across the layer, most of them sanctioned, and start
///         red — so this is a source-text scan, the same technique as <see cref="ProviderTelemetryWrapGuardTests" />.
///     </para>
///     <para>
///         Scope is the six service folders whose contract types were extracted into sibling files by the 2026-09
///         cleanup. It guards the regression where it actually happened rather than gating the whole layer; the rest of
///         <c>Services/</c> still holds pre-existing co-located declarations and is deliberately out of scope.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ServiceModelColocationTests
{
    private static readonly string[] ScannedFolders =
    [
        "AgentHome",
        "Benchmarks",
        "Development",
        "Integrations",
        "Training",
        "WorkSessions"
    ];

    // Filenames whose whole job is to declare contract types. A service class in one of these would be the inverse
    // mistake and is rare enough not to be worth a second rule.
    private static readonly string[] ContractFileSuffixes =
    [
        "Models",
        "ServiceModels",
        "Contracts",
        "Dtos"
    ];

    [Test]
    public void ServiceImplementationFiles_DoNotAlsoDeclareContractTypes()
    {
        var servicesRoot = RepositoryPaths.Combine("XE-Local-AI-Engine.Client.Application", "Services");
        AssertEx.True(Directory.Exists(servicesRoot), $"The scanned root '{servicesRoot}' must exist for this guard to mean anything.");

        var scanned = ScannedFolders
                      .Select(folder => Path.Combine(servicesRoot, folder))
                      .Where(Directory.Exists)
                      .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
                      .Select(path => new
                      {
                          RelativePath = Path.GetRelativePath(servicesRoot, path).Replace('\\', '/'),
                          FileName = Path.GetFileNameWithoutExtension(path),
                          Lines = File.ReadAllLines(path)
                      })
                      .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
                      .ToArray();

        // Non-vacuity: a renamed folder or a broken glob would otherwise report "no offenders" as a pass.
        AssertEx.Equal(ScannedFolders.Length,
            ScannedFolders.Count(folder => Directory.Exists(Path.Combine(servicesRoot, folder))),
            $"All six scanned service folders must exist under '{servicesRoot}'. A rename silently empties this guard.");
        AssertEx.True(scanned.Count(file => file.Lines.Any(DeclaresService)) >= 40,
            $"Expected at least forty service classes across the scanned folders; found {scanned.Count(file => file.Lines.Any(DeclaresService))}.");

        var offenders = scanned
                        .Where(file => !ContractFileSuffixes.Any(suffix => file.FileName.EndsWith(suffix, StringComparison.Ordinal)))
                        .Where(file => file.Lines.Any(DeclaresService) && file.Lines.Any(DeclaresTopLevelContractType))
                        .Select(static file => file.RelativePath)
                        .ToArray();

        AssertEx.Empty(offenders,
            "A service implementation file must not also declare the records and enums it exchanges: move them to a "
            + "sibling *Models / *ServiceModels / *Contracts / *Dtos file so a caller can find the contract without "
            + $"reading the implementation.{Environment.NewLine}Offending files: [{string.Join(", ", offenders)}]");
    }

    // A service class declaration at any nesting level. Leading whitespace is allowed so a nested or indented
    // declaration still counts.
    private static bool DeclaresService(string line)
    {
        var trimmed = line.TrimStart();

        return !trimmed.StartsWith("//", StringComparison.Ordinal)
               && trimmed.Contains(" class ", StringComparison.Ordinal)
               && ClassNameIn(trimmed) is { } name
               && name.EndsWith("Service", StringComparison.Ordinal);
    }

    // Only TOP-LEVEL public records and enums: a record nested inside the service class is scoped to it and is not
    // the boundary contract this rule is about. File-scoped namespaces put top-level declarations at column zero.
    private static bool DeclaresTopLevelContractType(string line) =>
        line.StartsWith("public record ", StringComparison.Ordinal)
        || line.StartsWith("public sealed record ", StringComparison.Ordinal)
        || line.StartsWith("public readonly record ", StringComparison.Ordinal)
        || line.StartsWith("public enum ", StringComparison.Ordinal);

    private static string? ClassNameIn(string trimmed)
    {
        var marker = trimmed.IndexOf(" class ", StringComparison.Ordinal);
        var rest = trimmed[(marker + " class ".Length)..];
        var end = rest.AsSpan().IndexOfAny(" :<{(".AsSpan());

        return end < 0 ? rest : rest[..end];
    }
}
