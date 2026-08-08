namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class MetadataToolFunctionTests
{
    private const string Schema = """{"type":"object","properties":{"goal":{"type":"string"}}}""";

    [Test]
    public void ExposesName_Description_AndSchema()
    {
        var function = new MetadataToolFunction("run_in_agent_home",
            "Runs a task.",
            MetadataToolFunction.ParseSchema(Schema),
            (_, _) => Task.FromResult("ok"));

        AssertEx.Equal("run_in_agent_home", function.Name);
        AssertEx.Equal("Runs a task.", function.Description);
        AssertEx.Equal("object", function.JsonSchema.GetProperty("type").GetString());
        AssertEx.True(function.JsonSchema.GetProperty("properties").TryGetProperty("goal", out _));
    }

    [Test]
    public async Task InvokeAsync_SerializesArgumentsToJsonForHandler()
    {
        string? captured = null;
        var function = new MetadataToolFunction("t",
            "d",
            MetadataToolFunction.ParseSchema(Schema),
            (json, _) =>
            {
                captured = json;
                return Task.FromResult("done");
            });

        var result = await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["goal"] = "analyze"
        }));

        AssertEx.NotNull(captured);
        AssertEx.Contains(captured!, "goal");
        AssertEx.Contains(captured!, "analyze");
        AssertEx.Equal("done", result?.ToString());
    }

    [Test]
    public async Task InvokeAsync_ForwardsCancellationTokenToHandler()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        var function = new MetadataToolFunction("t",
            "d",
            MetadataToolFunction.ParseSchema(Schema),
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult("done");
            });

        var threw = false;
        try
        {
            await function.InvokeAsync(new AIFunctionArguments(), cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        AssertEx.True(threw);
    }
}
