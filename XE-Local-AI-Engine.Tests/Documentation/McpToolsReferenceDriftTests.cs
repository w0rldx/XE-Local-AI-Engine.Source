namespace XE_Local_AI_Engine.Tests.Documentation;

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

        AssertEx.Equal(23, registered.Count, "Expected the exact 8 shared plus 15 admin inbound MCP tools.");
        AssertEx.Equal(8, registered.Count(static tool => tool.Value == "delegate"));
        AssertEx.Equal(15, registered.Count(static tool => tool.Value == "agentic"));
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

    private sealed record DocumentedTool(string Name, string Scope);
}
