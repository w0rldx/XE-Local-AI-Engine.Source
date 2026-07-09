namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ToolArgumentRepairAIFunctionTests
{
    private const string Schema = """
        {"type":"object","properties":{"path":{"type":"string"},"count":{"type":"integer"}},"required":["path"]}
        """;

    [Test]
    public async Task InvokeAsync_ValidArguments_PassThroughToHandlerUnchanged()
    {
        string? captured = null;
        var function = Build(json => captured = json, output: "handler-ran");

        var result = await function.InvokeAsync(Args(("path", "a.txt")));

        AssertEx.Equal("handler-ran", result as string);
        AssertEx.NotNull(captured);
        AssertEx.Contains(captured!, "a.txt");
    }

    [Test]
    public async Task InvokeAsync_MissingRequiredArgument_ReturnsRepairResultWithSchema_AndSkipsHandler()
    {
        var handlerCalled = false;
        var function = Build(_ => handlerCalled = true);

        var result = await function.InvokeAsync(Args(("count", 1L)));

        AssertEx.False(handlerCalled, "the handler must not run when arguments are invalid");
        using var document = JsonDocument.Parse(AsText(result));
        AssertEx.Equal("invalid_arguments", document.RootElement.GetProperty("error").GetString());
        AssertEx.Contains(document.RootElement.GetProperty("reason").GetString(), "path");
        AssertEx.True(document.RootElement.TryGetProperty("expected_schema", out var schema), "the repair result must carry the expected schema");
        AssertEx.True(schema.GetProperty("properties").TryGetProperty("path", out _), "the schema must describe the tool's properties");
        AssertEx.NotEmpty(document.RootElement.GetProperty("hint").GetString());
    }

    [Test]
    public async Task InvokeAsync_CoercibleArguments_AreRepairedThenReachTheHandler()
    {
        string? captured = null;
        var function = Build(json => captured = json);

        // "count" arrives as a quoted string; it must be coerced to a number before the handler sees it.
        var result = await function.InvokeAsync(Args(("path", "a.txt"), ("count", "5")));

        AssertEx.Equal("ok", result as string);
        AssertEx.NotNull(captured);
        AssertEx.Contains(captured!, "\"count\":5");
    }

    [Test]
    public async Task InvokeAsync_HandlerThrowsJsonException_ReturnsActionableResultNotThrow()
    {
        var function = Build(_ => throw new JsonException("bad shape"));

        var result = await function.InvokeAsync(Args(("path", "a.txt")));

        using var document = JsonDocument.Parse(AsText(result));
        AssertEx.Equal("invalid_arguments", document.RootElement.GetProperty("error").GetString());
    }

    [Test]
    public async Task InvokeAsync_RepeatedInvalidCalls_TripCapAndReturnTerminalDisabledResult()
    {
        var handlerCalls = 0;
        var function = Build(_ => handlerCalls++, maxInvalidCalls: 3);

        using (ToolArgumentRepairScope.BeginScope())
        {
            AssertEx.Equal("invalid_arguments", await ErrorAsync(function, Args(("count", 1L))));
            AssertEx.Equal("invalid_arguments", await ErrorAsync(function, Args(("count", 1L))));
            // The third consecutive invalid call hits the cap and returns the terminal result.
            AssertEx.Equal("tool_disabled", await ErrorAsync(function, Args(("count", 1L))));
            // Once disabled, further calls short-circuit — even a now-valid call is refused for this run.
            AssertEx.Equal("tool_disabled", await ErrorAsync(function, Args(("path", "a.txt"))));
        }

        AssertEx.Equal(0, handlerCalls);
    }

    [Test]
    public async Task InvokeAsync_ValidCall_ResetsConsecutiveInvalidStreak()
    {
        var function = Build(_ => { }, maxInvalidCalls: 3);

        using (ToolArgumentRepairScope.BeginScope())
        {
            AssertEx.Equal("invalid_arguments", await ErrorAsync(function, Args(("count", 1L))));
            AssertEx.Equal("invalid_arguments", await ErrorAsync(function, Args(("count", 1L))));
            // A valid call in between clears the streak...
            _ = await function.InvokeAsync(Args(("path", "a.txt")));
            // ...so the next invalid call is a repair, not the terminal result.
            AssertEx.Equal("invalid_arguments", await ErrorAsync(function, Args(("count", 1L))));
        }
    }

    [Test]
    public async Task InvokeAsync_CapState_IsIsolatedBetweenScopes()
    {
        var function = Build(_ => { }, maxInvalidCalls: 2);

        using (ToolArgumentRepairScope.BeginScope())
        {
            AssertEx.Equal("invalid_arguments", await ErrorAsync(function, Args(("count", 1L))));
            AssertEx.Equal("tool_disabled", await ErrorAsync(function, Args(("count", 1L))));
        }

        // A fresh request scope starts the tool's cap over from zero.
        using (ToolArgumentRepairScope.BeginScope())
        {
            AssertEx.Equal("invalid_arguments", await ErrorAsync(function, Args(("count", 1L))));
        }
    }

    [Test]
    public async Task InvokeAsync_WithoutScope_ReturnsRepairButNeverDisables()
    {
        var function = Build(_ => { }, maxInvalidCalls: 2);

        // No BeginScope and no function-invocation run: the cap cannot be tracked, so every invalid call is a repair.
        for (var i = 0; i < 5; i++)
        {
            AssertEx.Equal("invalid_arguments", await ErrorAsync(function, Args(("count", 1L))));
        }
    }

    private static async Task<string> ErrorAsync(AIFunction function, AIFunctionArguments arguments)
    {
        var result = await function.InvokeAsync(arguments);
        using var document = JsonDocument.Parse(AsText(result));
        return document.RootElement.GetProperty("error").GetString() ?? string.Empty;
    }

    private static string AsText(object? result)
    {
        return result as string ?? throw new AssertionException("Expected a string result.");
    }

    private static ToolArgumentRepairAIFunction Build(Action<string> onInvoke, int maxInvalidCalls = 3, string output = "ok")
    {
        var inner = new MetadataToolFunction("read_file",
            "Reads a file.",
            MetadataToolFunction.ParseSchema(Schema),
            (json, _) =>
            {
                onInvoke(json);
                return Task.FromResult(output);
            });

        return new ToolArgumentRepairAIFunction(inner, maxInvalidCalls);
    }

    private static AIFunctionArguments Args(params (string Key, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in pairs)
        {
            arguments[key] = value;
        }

        return arguments;
    }
}
