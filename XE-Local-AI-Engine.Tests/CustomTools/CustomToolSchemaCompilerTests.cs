namespace XE_Local_AI_Engine.Tests.CustomTools;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Schema compiler: the emitted schema carries no GBNF-breaking length/range keyword, a Fixed tool compiles to a
///     closed empty object, and a Parameterized tool exposes its declared properties + required set.
/// </summary>
public sealed class CustomToolSchemaCompilerTests
{
    [Test]
    public async Task Compile_ParameterizedSchema_ContainsNoBannedGbnfKeyword()
    {
        IReadOnlyList<CustomToolParameter> parameters =
        [
            new CustomToolParameter("city", "string", "City name", Required: true),
            new CustomToolParameter("limit", "integer", "Max results", Required: false)
        ];

        var schema = CustomToolSchemaCompiler.Compile(CustomToolMode.Parameterized, parameters);

        foreach (var banned in CustomToolSchemaCompiler.BannedSchemaKeywords)
        {
            AssertEx.False(schema.Contains(banned, StringComparison.Ordinal), $"Schema must not contain the GBNF-breaking keyword '{banned}'. Schema: {schema}");
        }

        // Parses as a valid object schema exposing the declared properties + required.
        using var document = JsonDocument.Parse(schema);
        var root = document.RootElement;
        AssertEx.Equal("object", root.GetProperty("type").GetString());
        AssertEx.True(root.GetProperty("properties").TryGetProperty("city", out _));
        AssertEx.True(root.GetProperty("properties").TryGetProperty("limit", out _));
        AssertEx.Contains(root.GetProperty("required").EnumerateArray().Select(static element => element.GetString()!), "city");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compile_FixedMode_EmitsClosedEmptyObject()
    {
        IReadOnlyList<CustomToolParameter> ignored = [new CustomToolParameter("ignored", "string", "unused", Required: true)];
        var schema = CustomToolSchemaCompiler.Compile(CustomToolMode.Fixed, ignored);

        using var document = JsonDocument.Parse(schema);
        var root = document.RootElement;
        AssertEx.Equal("object", root.GetProperty("type").GetString());
        AssertEx.Empty(root.GetProperty("properties").EnumerateObject());
        AssertEx.False(root.GetProperty("additionalProperties").GetBoolean());
        await Task.CompletedTask;
    }
}
