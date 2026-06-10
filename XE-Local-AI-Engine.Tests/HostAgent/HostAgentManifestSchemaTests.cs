namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Globalization;
using System.Text.Json.Nodes;
using NJsonSchema;
using NJsonSchema.Validation;
using XE_Local_AI_Engine.Tests.Testing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

public sealed class HostAgentManifestSchemaTests
{
    private const string SchemaFixturePath = "Fixtures/HostAgent/host-agent-manifest.schema.json";

    private static readonly string[] SampleManifestFixturePaths =
    [
        "Fixtures/HostAgent/SampleManifests/managed.yaml",
        "Fixtures/HostAgent/SampleManifests/byo.yaml"
    ];

    [Test]
    public async Task SampleManifests_ValidateAgainstHostAgentSchema()
    {
        var schema = await LoadSchemaAsync();

        foreach (var manifestPath in SampleManifestFixturePaths)
        {
            var yaml = await File.ReadAllTextAsync(GetFixturePath(manifestPath));
            var errors = ValidateYaml(schema, yaml);

            AssertEx.Empty(errors, $"Expected {manifestPath} to match the HostAgent manifest schema: {FormatErrors(errors)}");
        }
    }

    [Test]
    public async Task ManifestSchema_WhenImageUsesLatestTag_RejectsManifest()
    {
        var schema = await LoadSchemaAsync();
        var yaml = await File.ReadAllTextAsync(GetFixturePath(SampleManifestFixturePaths[0]));
        var latestTagYaml = ReplaceOllamaImage(yaml, "ollama/ollama:latest@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var errors = ValidateYaml(schema, latestTagYaml);

        AssertHasImagePatternError(errors);
    }

    [Test]
    public async Task ManifestSchema_WhenImageOmitsDigest_RejectsManifest()
    {
        var schema = await LoadSchemaAsync();
        var yaml = await File.ReadAllTextAsync(GetFixturePath(SampleManifestFixturePaths[0]));
        var missingDigestYaml = ReplaceOllamaImage(yaml, "ollama/ollama:0.30.5");

        var errors = ValidateYaml(schema, missingDigestYaml);

        AssertHasImagePatternError(errors);
    }

    /// <summary>
    ///     Rewrites the ollama container image reference in the sample manifest without depending on the
    ///     exact version/digest currently pinned there, so digest/version bumps don't break the negative tests.
    /// </summary>
    private static string ReplaceOllamaImage(string yaml, string replacementImage)
    {
        var mutated = System.Text.RegularExpressions.Regex.Replace(
            yaml,
            "ollama/ollama:[^\"@]+@sha256:[0-9a-f]{64}",
            replacementImage);
        AssertEx.True(!string.Equals(mutated, yaml, StringComparison.Ordinal), "Expected the sample manifest to contain a pinned ollama image to mutate.");
        return mutated;
    }

    private static async Task<JsonSchema> LoadSchemaAsync()
    {
        var schemaJson = await File.ReadAllTextAsync(GetFixturePath(SchemaFixturePath));
        return await JsonSchema.FromJsonAsync(schemaJson);
    }

    private static IReadOnlyCollection<ValidationError> ValidateYaml(JsonSchema schema, string yaml)
    {
        var yamlStream = new YamlStream();
        yamlStream.Load(new StringReader(yaml));

        var json = ConvertYamlNodeToJson(yamlStream.Documents[0].RootNode).ToJsonString();

        return schema.Validate(json).ToArray();
    }

    private static JsonNode ConvertYamlNodeToJson(YamlNode yamlNode)
    {
        return yamlNode switch
        {
            YamlMappingNode mapping => ConvertMappingToJson(mapping),
            YamlSequenceNode sequence => ConvertSequenceToJson(sequence),
            YamlScalarNode scalar => ConvertScalarToJson(scalar),
            _ => throw new NotSupportedException($"Unsupported YAML node type: {yamlNode.GetType().Name}")
        };
    }

    private static JsonObject ConvertMappingToJson(YamlMappingNode mapping)
    {
        var json = new JsonObject();

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = ((YamlScalarNode)keyNode).Value
                      ?? throw new InvalidOperationException("YAML mapping key must not be null.");
            json[key] = ConvertYamlNodeToJson(valueNode);
        }

        return json;
    }

    private static JsonArray ConvertSequenceToJson(YamlSequenceNode sequence)
    {
        var json = new JsonArray();

        foreach (var child in sequence.Children)
        {
            json.Add(ConvertYamlNodeToJson(child));
        }

        return json;
    }

    private static JsonValue ConvertScalarToJson(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;

        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
        {
            return JsonValue.Create(value);
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return JsonValue.Create(boolValue);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return JsonValue.Create(longValue);
        }

        return JsonValue.Create(value);
    }

    private static void AssertHasImagePatternError(IReadOnlyCollection<ValidationError> errors)
    {
        AssertEx.Contains(errors,
            error => (error.Path?.Contains("image", StringComparison.OrdinalIgnoreCase) == true
                      || error.ToString().Contains("image", StringComparison.OrdinalIgnoreCase))
                     && error.ToString().Contains("pattern", StringComparison.OrdinalIgnoreCase),
            $"Expected an image pattern validation error: {FormatErrors(errors)}");
    }

    private static string GetFixturePath(string relativePath)
    {
        return Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string FormatErrors(IEnumerable<ValidationError> errors)
    {
        return string.Join(Environment.NewLine, errors.Select(error => $"{error.Path}: {error.Kind}"));
    }
}
