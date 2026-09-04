namespace XE_Local_AI_Engine.Tests.Documentation;

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Services.Mcp.Server;
using XE_Local_AI_Engine.Tests.Testing;

public sealed partial class McpToolsReferenceDriftTests
{
    [Test]
    public void McpToolsReference_NamesAndScopesExactlyMatchRegisteredTools()
    {
        var registered = RegisteredTools(typeof(NodeAgentMcpTools), "delegate")
                         .Concat(RegisteredTools(typeof(NodeAdminMcpTools), "agentic"))
                         .ToDictionary(static tool => tool.Name, static tool => tool.Scope, StringComparer.Ordinal);
        var documented = ParseDocumentedTools(RepositoryPaths.Combine("skills",
            "xe-local-ai-engine",
            "references",
            "mcp-tools.md"));

        AssertEx.Equal(25, registered.Count, "Expected the exact 8 shared plus 17 admin inbound MCP tools.");
        AssertEx.Equal(8, registered.Count(static tool => tool.Value == "delegate"));
        AssertEx.Equal(17, registered.Count(static tool => tool.Value == "agentic"));
        AssertEx.NotEmpty(documented, "The MCP reference contained no parsed tool rows; refusing a vacuous drift pass.");

        var missingFromDocumentation = registered.Keys.Except(documented.Keys, StringComparer.Ordinal).Order().ToArray();
        var missingFromRegistration = documented.Keys.Except(registered.Keys, StringComparer.Ordinal).Order().ToArray();
        AssertEx.Empty(missingFromDocumentation,
            $"Registered MCP tools missing from references/mcp-tools.md: {string.Join(", ", missingFromDocumentation)}");
        AssertEx.Empty(missingFromRegistration,
            $"Documented MCP tools not registered: {string.Join(", ", missingFromRegistration)}");

        var wrongScopes = registered.Where(tool => !documented.TryGetValue(tool.Key, out var scope)
                                                   || !string.Equals(tool.Value, scope, StringComparison.Ordinal))
                                    .Select(tool => $"{tool.Key}: expected {tool.Value}, documented {documented.GetValueOrDefault(tool.Key, "<missing>")}")
                                    .Order()
                                    .ToArray();
        AssertEx.Empty(wrongScopes, $"Documented MCP tool scopes are wrong: {string.Join("; ", wrongScopes)}");
    }

    [Test]
    public void McpToolsReference_SettingsWhitelistMatchesTheUpdateNodeSettingsParameters()
    {
        // The tool table above pins names and scopes; this pins the one tool whose CONTRACT is a field list. Without
        // it a new whitelisted setting silently leaves the reference claiming a stale count and a stale list.
        var registered = AssertEx.NotNull(typeof(NodeAdminMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                                                   .SingleOrDefault(static method =>
                                                                       method.GetCustomAttribute<McpServerToolAttribute>()?.Name == "update_node_settings"))
                                 .GetParameters()
                                 .Where(static parameter => parameter.ParameterType != typeof(CancellationToken))
                                 .Select(static parameter => parameter.Name!)
                                 .Order(StringComparer.Ordinal)
                                 .ToArray();

        var reference = File.ReadAllText(RepositoryPaths.Combine("skills",
            "xe-local-ai-engine",
            "references",
            "mcp-tools.md"));
        var whitelist = AssertEx.NotNull(SettingsWhitelistRegex().Match(reference) is { Success: true } match ? match : null);
        var documented = SettingsFieldRegex().Matches(whitelist.Groups["fields"].Value)
                                             .Select(static field => field.Groups["field"].Value)
                                             .Order(StringComparer.Ordinal)
                                             .ToArray();

        AssertEx.Equal(registered.Length, int.Parse(whitelist.Groups["count"].Value, CultureInfo.InvariantCulture),
            "The reference's field COUNT drifted from update_node_settings.");
        AssertEx.Equal(string.Join('|', registered), string.Join('|', documented),
            "The reference's field LIST drifted from update_node_settings.");
    }

    [Test]
    public void McpToolsReference_SettingsWhitelist_MatchesTheUpdateToolParameters()
    {
        var registered = typeof(NodeAdminMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                                  .Single(static method =>
                                                      method.GetCustomAttribute<McpServerToolAttribute>()?.Name
                                                      == "update_node_settings")
                                                  .GetParameters()
                                                  .Where(static parameter => parameter.ParameterType != typeof(CancellationToken))
                                                  .Select(static parameter => parameter.Name!)
                                                  .ToArray();
        var reference = RepositoryPaths.Combine("skills", "xe-local-ai-engine", "references", "mcp-tools.md");
        var referenceText = File.ReadAllText(reference);
        var documented = WhitelistFieldsRegex().Matches(WhitelistSection(referenceText))
                                               .Select(static match => match.Groups["field"].Value)
                                               .ToArray();

        AssertEx.NotEmpty(documented, "The MCP reference contained no parsed whitelist fields; refusing a vacuous drift pass.");
        AssertEx.Empty(registered.Except(documented, StringComparer.Ordinal).Order().ToArray(),
            $"update_node_settings parameters missing from the documented whitelist: {string.Join(", ", registered.Except(documented, StringComparer.Ordinal).Order())}");
        AssertEx.Empty(documented.Except(registered, StringComparer.Ordinal).Order().ToArray(),
            $"Documented whitelist fields that update_node_settings does not accept: {string.Join(", ", documented.Except(registered, StringComparer.Ordinal).Order())}");

        // The two prose counts drift independently of the list itself, so they are pinned separately.
        var count = registered.Length;
        AssertEx.Contains(referenceText, $"the exact {count}-field whitelist");
        AssertEx.Contains(referenceText, $"accepts only these {count} optional fields");
        AssertEx.Contains(File.ReadAllText(RepositoryPaths.Combine("skills", "xe-local-ai-engine", "SKILL.md")),
            $"outside the {count}-field whitelist");
    }

    private static string WhitelistSection(string referenceText)
    {
        // Slice from after the count sentence so the tool's own name is not read as a whitelist field.
        var start = referenceText.IndexOf("optional fields:", StringComparison.Ordinal);
        AssertEx.True(start >= 0, "The MCP reference has no settings-whitelist field list.");
        var end = referenceText.IndexOf("`CustomToolsEnabled`", start, StringComparison.Ordinal);
        AssertEx.True(end > start, "The MCP reference whitelist section has no terminating CustomToolsEnabled paragraph.");
        return referenceText[start..end];
    }

    private static IEnumerable<DocumentedTool> RegisteredTools(Type toolType, string scope) =>
        toolType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Select(static method => (Method: method,
                    Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
                .Where(static item => item.Attribute is not null)
                .Select(item => new DocumentedTool(item.Attribute!.Name ?? item.Method.Name, scope));

    private static Dictionary<string, string> ParseDocumentedTools(string path)
    {
        var documented = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var match = ToolCellRegex().Match(line);
            if (match.Success && !string.Equals(match.Groups["tool"].Value, "tool", StringComparison.Ordinal))
            {
                documented.Add(match.Groups["tool"].Value, match.Groups["scope"].Value);
            }
        }

        return documented;
    }

    [GeneratedRegex(@"^\|\s*`(?<tool>[a-z0-9_]+)`\s*\|\s*(?<scope>delegate|agentic)\s*\|")]
    private static partial Regex ToolCellRegex();

    [GeneratedRegex(@"accepts only these (?<count>\d+) optional fields:(?<fields>[\s\S]*?)\.\n")]
    private static partial Regex SettingsWhitelistRegex();

    [GeneratedRegex(@"`(?<field>[a-z0-9_]+)`")]
    private static partial Regex SettingsFieldRegex();

    [GeneratedRegex(@"`(?<field>[a-z][a-z0-9_]*_[a-z0-9_]+)`")]
    private static partial Regex WhitelistFieldsRegex();

    private sealed record DocumentedTool(string Name, string Scope);
}
