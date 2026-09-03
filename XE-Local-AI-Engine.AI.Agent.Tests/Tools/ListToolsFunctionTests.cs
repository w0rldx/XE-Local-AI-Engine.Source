namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The escape hatch itself. The listing is the easy half; the reveal is the one that has to land on the RIGHT
///     array, which is why the function is bound to a decision object rather than looking one up by key — there is no
///     ambient "current array" slot to race, and a signature assertion below is what keeps one from coming back.
/// </summary>
public sealed class ListToolsFunctionTests
{
    [Test]
    public async Task InvokeAsync_ReturnsExactlyTheHiddenNamesWithOneLineDescriptions()
    {
        var tools = Tools(("read_file", "Reads a file."), ("deploy", "Deploys the release."), ("search", "Searches."));
        var function = new ListToolsFunction(tools);
        function.Bind(Decision(offered: ["read_file"], hidden: ["deploy", "search"]));

        var listed = await ListAsync(function);

        AssertEx.Equal(expected: 2, listed.Count);
        AssertEx.Equal("deploy", listed[0].Name);
        AssertEx.Equal("Deploys the release.", listed[0].Description);
        AssertEx.Equal("search", listed[1].Name);
    }

    [Test]
    public async Task InvokeAsync_TruncatesALongDescriptionAndFlattensItToOneLine()
    {
        var tools = Tools(("verbose", $"first line{Environment.NewLine}{new string('x', 400)}"));
        var function = new ListToolsFunction(tools);
        function.Bind(Decision(offered: [], hidden: ["verbose"]));

        var listed = await ListAsync(function);

        AssertEx.Equal(ListToolsFunction.MaxDescriptionLength, listed[0].Description.Length, "A listing is a menu, not a second copy of the schema.");
        AssertEx.False(listed[0].Description.Contains('\n', StringComparison.Ordinal), "One line each, so a menu does not become a wall of text.");
    }

    [Test]
    public async Task InvokeAsync_WhenNothingIsHidden_ReturnsAnEmptyArray()
    {
        var function = new ListToolsFunction(Tools(("read_file", "Reads a file.")));
        function.Bind(Decision(offered: ["read_file"], hidden: []));

        AssertEx.Equal("[]", await function.InvokeAsync(new AIFunctionArguments()) as string);
    }

    [Test]
    public async Task InvokeAsync_WithNoBoundDecision_ReturnsAnEmptyArrayAndRevealsNothing()
    {
        var function = new ListToolsFunction(Tools(("read_file", "Reads a file.")));

        AssertEx.Equal("[]", await function.InvokeAsync(new AIFunctionArguments()) as string);
        AssertEx.Null(function.BoundDecision, "An unbound invocation is defined, not exceptional: no throw, no ambient lookup.");
    }

    [Test]
    public async Task InvokeAsync_WithTwoDecisionsBound_RevealsOnlyOnTheMostRecentlyBoundOne()
    {
        var function = new ListToolsFunction(Tools(("deploy", "Deploys."), ("search", "Searches.")));
        var superseded = Decision(offered: [], hidden: ["deploy"]);
        var current = Decision(offered: [], hidden: ["search"]);

        function.Bind(superseded);
        function.Bind(current);
        _ = await function.InvokeAsync(new AIFunctionArguments());

        AssertEx.True(current.IsRevealed("search"), "The reveal lands on the decision for the array the model was looking at.");
        AssertEx.False(superseded.IsRevealed("deploy"), "A superseded decision is never written to.");
    }

    [Test]
    public void InvokeAsync_TakesNoArguments_AndReadsNoAmbientKey()
    {
        // A signature assertion, deliberately: zero declared parameters is what rules out an ambient current-array
        // argument — the shape whose race the object binding exists to remove.
        var function = new ListToolsFunction(Tools(("read_file", "Reads a file.")));

        var properties = function.JsonSchema.GetProperty("properties");

        AssertEx.Equal(expected: 0, properties.EnumerateObject().Count());
        AssertEx.Equal("list_tools", function.Name);
        AssertEx.Equal("list_tools", ListToolsFunction.ToolName);
    }

    private static async Task<List<HiddenToolView>> ListAsync(ListToolsFunction function)
    {
        var json = await function.InvokeAsync(new AIFunctionArguments()) as string;
        return JsonSerializer.Deserialize<List<HiddenToolView>>(AssertEx.NotNull(json), JsonSerializerOptions.Web) ?? [];
    }

    private static List<AITool> Tools(params (string Name, string Description)[] tools)
    {
        return [.. tools.Select(static tool => AIFunctionFactory.Create(() => "ok", tool.Name, tool.Description))];
    }

    private static ArrayDecision Decision(string[] offered, string[] hidden)
    {
        return new ArrayDecision
        {
            OfferedNames = offered,
            HiddenNames = hidden
        };
    }

    private sealed record HiddenToolView(string Name, string Description);
}
