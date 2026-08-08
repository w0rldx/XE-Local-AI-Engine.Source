namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalAgentToolRegistryTests
{
    [Test]
    public void GetLocalChatTools_ReturnsTimeAndCalculatorFunctions()
    {
        var registry = new LocalAgentToolRegistry();

        var tools = registry.GetLocalChatTools();

        AssertEx.Equal(expected: 2, tools.Count);
        AssertEx.Contains(tools.OfType<AIFunction>().Select(static tool => tool.Name), "GetCurrentTime");
        AssertEx.Contains(tools.OfType<AIFunction>().Select(static tool => tool.Name), "Calculate");
    }

    [Test]
    public void GetLocalChatToolDescriptors_MirrorsToolsWithSchemaAndAutoExecute()
    {
        var registry = new LocalAgentToolRegistry();

        var descriptors = registry.GetLocalChatToolDescriptors();

        AssertEx.Equal(expected: 2, descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            AssertEx.NotNullOrEmpty(descriptor.Name);
            AssertEx.NotNullOrEmpty(descriptor.Description);
            AssertEx.NotNullOrEmpty(descriptor.ParameterSchema);
            AssertEx.False(descriptor.RequiresApproval);
        }

        var toolNames = registry.GetLocalChatTools().OfType<AIFunction>().Select(static tool => tool.Name).ToHashSet();
        foreach (var descriptor in descriptors)
        {
            AssertEx.Contains(toolNames, descriptor.Name);
        }
    }

    [Test]
    public async Task Calculate_EvaluatesArithmeticExpression()
    {
        var calculate = GetTool("Calculate");

        var result = await InvokeAsync(calculate, new Dictionary<string, object?>
        {
            ["expression"] = "12 * 9"
        });

        AssertEx.Contains(result, "108");
    }

    [Test]
    public async Task Calculate_RejectsNonArithmeticExpression()
    {
        var calculate = GetTool("Calculate");

        var result = await InvokeAsync(calculate, new Dictionary<string, object?>
        {
            ["expression"] = "drop table users"
        });

        AssertEx.Contains(result, "Unable to evaluate", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task GetCurrentTime_ReturnsUtcLocalAndDate()
    {
        var time = GetTool("GetCurrentTime");

        var result = await InvokeAsync(time, new Dictionary<string, object?>());

        AssertEx.Contains(result, "UTC time:");
        AssertEx.Contains(result, "Local time:");
        AssertEx.Contains(result, "Today's date:");
    }

    [Test]
    public async Task GetCurrentTime_WithUnknownTimezone_FallsBackToLocalAndNotes()
    {
        var time = GetTool("GetCurrentTime");

        var result = await InvokeAsync(time, new Dictionary<string, object?>
        {
            ["timezone"] = "Not/AZone"
        });

        AssertEx.Contains(result, "was not recognized", StringComparison.OrdinalIgnoreCase);
    }

    private static AIFunction GetTool(string name)
    {
        var registry = new LocalAgentToolRegistry();
        return registry.GetLocalChatTools()
                       .OfType<AIFunction>()
                       .Single(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
    }

    private static async Task<string> InvokeAsync(AIFunction function, IDictionary<string, object?> arguments)
    {
        var result = await function.InvokeAsync(new AIFunctionArguments(arguments));
        return result?.ToString() ?? string.Empty;
    }
}
