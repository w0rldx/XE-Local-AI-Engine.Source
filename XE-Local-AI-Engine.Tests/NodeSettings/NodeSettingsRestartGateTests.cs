namespace XE_Local_AI_Engine.Tests.NodeSettings;

using System.Text.Json;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The DTO docs and the gate must name the same fields: a doc that says "Applies after a node restart" for a field
///     read live (or the reverse) is how the SPA badge and the backend drifted apart before the gate existed.
/// </summary>
[Category(TestCategories.Unit)]
public sealed partial class NodeSettingsRestartGateTests
{
    private const string RestartClaim = "applies after a node restart";

    [Test]
    [Arguments("NodeSettingsResponse")]
    [Arguments("SaveNodeSettingsRequest")]
    public async Task DtoDocs_ClaimARestart_ForExactlyTheGatedFields(string recordName)
    {
        var source = await File.ReadAllLinesAsync(RepositoryPaths.ClientProject("Endpoints", "NodeSettings", "V1", "Dtos", "NodeSettingsEndpointDtos.cs"));

        var documented = RestartDocumentedProperties(source, recordName);

        var gate = NodeSettingsRestartGate.Fields.ToHashSet(StringComparer.Ordinal);
        string[] docOnly = [.. documented.Where(field => !gate.Contains(field)).Order(StringComparer.Ordinal)];
        string[] gateOnly = [.. gate.Where(field => !documented.Contains(field)).Order(StringComparer.Ordinal)];
        AssertEx.True(docOnly.Length == 0 && gateOnly.Length == 0,
            $"{recordName}: the doc claims a restart the gate lacks [{string.Join(", ", docOnly)}]; "
            + $"the gate lists fields the doc does not claim [{string.Join(", ", gateOnly)}].");
    }

    [Test]
    public void Fields_AreSortedAndUnique()
    {
        AssertEx.Equal(string.Join(',', NodeSettingsRestartGate.Fields.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            string.Join(',', NodeSettingsRestartGate.Fields));
    }

    // Walks the record's members: a /// run attaches to the next property declaration, and any other line ends the run.
    private static HashSet<string> RestartDocumentedProperties(IReadOnlyList<string> source, string recordName)
    {
        var documented = new HashSet<string>(StringComparer.Ordinal);
        var inRecord = false;
        var docRun = new List<string>();
        foreach (var line in source.Select(raw => raw.Trim()))
        {
            if (RecordDeclaration().Match(line) is { Success: true } record)
            {
                inRecord = string.Equals(record.Groups[1].Value, recordName, StringComparison.Ordinal);
                docRun.Clear();
                continue;
            }

            if (line.StartsWith("///", StringComparison.Ordinal))
            {
                docRun.Add(line);
                continue;
            }

            if (inRecord
                && PropertyDeclaration().Match(line) is { Success: true } property
                && string.Join(' ', docRun).Contains(RestartClaim, StringComparison.OrdinalIgnoreCase))
            {
                documented.Add(JsonNamingPolicy.CamelCase.ConvertName(property.Groups[1].Value));
            }

            if (!line.StartsWith('['))
            {
                docRun.Clear();
            }
        }

        AssertEx.True(documented.Count > 0, $"No '{RestartClaim}' doc found in {recordName}: the parser no longer matches the DTO file.");
        return documented;
    }

    [GeneratedRegex(@"^public sealed record (\w+)")]
    private static partial Regex RecordDeclaration();

    [GeneratedRegex(@"^public [^=(]+? (\w+) \{ get;")]
    private static partial Regex PropertyDeclaration();
}
