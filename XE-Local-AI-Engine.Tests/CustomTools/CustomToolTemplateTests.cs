namespace XE_Local_AI_Engine.Tests.CustomTools;

using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Parameter binding + single-value substitution: a substituted value is one value (never split), an undeclared
///     placeholder or a type mismatch is a fail-closed rejection, and a required-but-absent value throws.
/// </summary>
public sealed class CustomToolTemplateTests
{
    private static readonly IReadOnlyList<CustomToolParameter> StringParam =
    [
        new CustomToolParameter("q", "string", "query", Required: true)
    ];

    [Test]
    public async Task Substitute_KeepsInjectionPayloadAsOneValue()
    {
        // The classic argv-injection payload must survive as a SINGLE opaque value — never whitespace-split, never a
        // shell string. The whole substituted result is one argv element.
        var bound = CustomToolTemplate.BindAndEnforce("""{"q":"a b; rm -rf /"}""", StringParam);
        var result = CustomToolTemplate.Substitute("{q}", bound, new HashSet<string>(["q"]));

        AssertEx.Equal("a b; rm -rf /", result);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Substitute_UndeclaredPlaceholder_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolTemplate.Substitute("{q} {other}", new Dictionary<string, string>
        {
            ["q"] = "x"
        }, new HashSet<string>(["q"])));
        await Task.CompletedTask;
    }

    [Test]
    public async Task BindAndEnforce_NumberParamGivenString_Rejects()
    {
        IReadOnlyList<CustomToolParameter> numberParam = [new CustomToolParameter("n", "number", "count", Required: true)];
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolTemplate.BindAndEnforce("""{"n":"notanumber"}""", numberParam));
        await Task.CompletedTask;
    }

    [Test]
    public async Task BindAndEnforce_NumberParamGivenNumber_Passes()
    {
        IReadOnlyList<CustomToolParameter> numberParam = [new CustomToolParameter("n", "integer", "count", Required: true)];
        var bound = CustomToolTemplate.BindAndEnforce("""{"n":42}""", numberParam);
        AssertEx.Equal("42", bound["n"]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task BindAndEnforce_RequiredParamMissing_Rejects()
    {
        AssertEx.Throws<CustomToolExecutionException>(() => CustomToolTemplate.BindAndEnforce("{}", StringParam));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Substitute_WithUrlEncoder_EscapesStructureBreakingCharacters()
    {
        var bound = CustomToolTemplate.BindAndEnforce("""{"q":"a/../b?x=1"}""", StringParam);
        var result = CustomToolTemplate.Substitute("https://api.example.com/search?term={q}", bound, new HashSet<string>(["q"]), Uri.EscapeDataString);

        AssertEx.False(result.Contains("a/../b", StringComparison.Ordinal), "The value must be URL-encoded so it cannot alter the URL structure.");
        AssertEx.Contains(result, "a%2F..%2Fb");
        await Task.CompletedTask;
    }
}
