namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ToolArgumentValidatorTests
{
    private const string ObjectSchema = """
                                        {"type":"object","properties":{"path":{"type":"string"},"count":{"type":"integer"},"ratio":{"type":"number"},"enabled":{"type":"boolean"},"tags":{"type":"array"}},"required":["path"]}
                                        """;

    [Test]
    public void CoerceAndValidate_AllValid_ReturnsValid()
    {
        var arguments = Args(("path", "a.txt"), ("count", 3L), ("enabled", true));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.True(result.IsValid, result.Reason);
    }

    [Test]
    public void CoerceAndValidate_MissingRequiredProperty_ReturnsInvalidNamingIt()
    {
        var arguments = Args(("count", 3L));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.False(result.IsValid);
        AssertEx.Contains(result.Reason, "path");
        AssertEx.Contains(result.Reason, "missing");
    }

    [Test]
    public void CoerceAndValidate_RequiredPropertyPresentButNull_TreatedAsMissing()
    {
        var arguments = Args(("path", null));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.False(result.IsValid);
        AssertEx.Contains(result.Reason, "path");
    }

    [Test]
    public void CoerceAndValidate_UnknownProperty_ReturnsInvalid()
    {
        var arguments = Args(("path", "a.txt"), ("bogus", "x"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.False(result.IsValid);
        AssertEx.Contains(result.Reason, "bogus");
    }

    [Test]
    public void CoerceAndValidate_UnknownProperty_AllowedWhenAdditionalPropertiesTrue()
    {
        const string schema = """{"type":"object","properties":{"path":{"type":"string"}},"additionalProperties":true}""";
        var arguments = Args(("path", "a.txt"), ("extra", "ok"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(schema), arguments);

        AssertEx.True(result.IsValid, result.Reason);
    }

    [Test]
    public void CoerceAndValidate_UnknownProperty_AllowedWhenRejectUnknownPropertiesFalse()
    {
        // Non-strict mode (third-party MCP tools): an undeclared key passes through rather than being rejected.
        var arguments = Args(("path", "a.txt"), ("undeclared", "x"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments, rejectUnknownProperties: false);

        AssertEx.True(result.IsValid, result.Reason);
    }

    [Test]
    public void CoerceAndValidate_NonStrict_StillEnforcesRequiredProperties()
    {
        // Relaxing the unknown-property check must not relax the required check.
        var arguments = Args(("count", 3L), ("undeclared", "x"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments, rejectUnknownProperties: false);

        AssertEx.False(result.IsValid);
        AssertEx.Contains(result.Reason, "path");
        AssertEx.Contains(result.Reason, "missing");
    }

    [Test]
    public void CoerceAndValidate_NonStrict_StillEnforcesDeclaredTypes()
    {
        // A declared property with the wrong type is still rejected in non-strict mode.
        var arguments = Args(("path", "a.txt"), ("count", new Dictionary<string, object?>()), ("undeclared", "x"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments, rejectUnknownProperties: false);

        AssertEx.False(result.IsValid);
        AssertEx.Contains(result.Reason, "count");
    }

    [Test]
    public void CoerceAndValidate_TypeMismatch_ReturnsInvalidNamingProperty()
    {
        // "count" is an integer; an object cannot be coerced and must be reported.
        var arguments = Args(("path", "a.txt"), ("count", new Dictionary<string, object?>()));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.False(result.IsValid);
        AssertEx.Contains(result.Reason, "count");
    }

    [Test]
    public void CoerceAndValidate_NumericStringForInteger_CoercedToLongAndValid()
    {
        var arguments = Args(("path", "a.txt"), ("count", "42"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.True(result.IsValid, result.Reason);
        AssertEx.True(arguments["count"] is 42L, "the numeric string must be coerced to a long");
    }

    [Test]
    public void CoerceAndValidate_NumericStringForNumber_CoercedToDouble()
    {
        var arguments = Args(("path", "a.txt"), ("ratio", "1.5"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.True(result.IsValid, result.Reason);
        AssertEx.True(arguments["ratio"] is 1.5d, "the numeric string must be coerced to a double");
    }

    [Test]
    public void CoerceAndValidate_BooleanString_Coerced()
    {
        var arguments = Args(("path", "a.txt"), ("enabled", "true"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.True(result.IsValid, result.Reason);
        AssertEx.True(arguments["enabled"] is true, "the boolean string must be coerced to a bool");
    }

    [Test]
    public void CoerceAndValidate_SingleValueForArray_WrappedInArrayOfOne()
    {
        var arguments = Args(("path", "a.txt"), ("tags", "solo"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.True(result.IsValid, result.Reason);
        var wrapped = arguments["tags"] as IReadOnlyList<object?> ?? throw new AssertionException("expected an array");
        AssertEx.Equal(1, wrapped.Count);
        AssertEx.Equal("solo", wrapped[0] as string);
    }

    [Test]
    public void CoerceAndValidate_JsonElementArguments_AreUnderstood()
    {
        using var document = JsonDocument.Parse("""{"path":"a.txt","count":7}""");
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        var result = ToolArgumentValidator.CoerceAndValidate(Schema(ObjectSchema), arguments);

        AssertEx.True(result.IsValid, result.Reason);
    }

    [Test]
    public void CoerceAndValidate_NonObjectSchema_ReturnsValid()
    {
        var arguments = Args(("anything", "goes"));

        var result = ToolArgumentValidator.CoerceAndValidate(Schema("""{"type":"string"}"""), arguments);

        AssertEx.True(result.IsValid, result.Reason);
    }

    [Test]
    public void CoerceAndValidate_SchemaWithoutProperties_OnlyEnforcesRequired()
    {
        const string schema = """{"type":"object","required":["path"]}""";

        var missing = ToolArgumentValidator.CoerceAndValidate(Schema(schema), Args(("other", "x")));
        AssertEx.False(missing.IsValid);

        var present = ToolArgumentValidator.CoerceAndValidate(Schema(schema), Args(("path", "a.txt"), ("other", "x")));
        AssertEx.True(present.IsValid, present.Reason);
    }

    private static JsonElement Schema(string json)
    {
        return MetadataToolFunction.ParseSchema(json);
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            arguments[key] = value;
        }

        return arguments;
    }
}
