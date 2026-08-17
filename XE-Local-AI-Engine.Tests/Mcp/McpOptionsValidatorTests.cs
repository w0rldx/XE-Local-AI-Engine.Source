namespace XE_Local_AI_Engine.Tests.Mcp;

using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The MCP options validator rejects a non-positive per-call tool timeout (alongside the existing connect
///     timeout and loopback-host checks), so a misconfiguration fails fast at startup.
/// </summary>
public sealed class McpOptionsValidatorTests
{
    [Test]
    public void Validate_WithDefaults_Succeeds()
    {
        var result = new McpOptionsValidator().Validate(name: null, new McpOptions());

        AssertEx.True(result.Succeeded, "Default MCP options must validate.");
    }

    [Test]
    public void Validate_WhenToolCallTimeoutIsZero_Fails()
    {
        var result = new McpOptionsValidator().Validate(name: null, new McpOptions
        {
            ToolCallTimeoutSeconds = 0
        });

        AssertEx.True(result.Failed, "A non-positive ToolCallTimeoutSeconds must fail validation.");
        AssertEx.True(result.FailureMessage?.Contains("ToolCallTimeoutSeconds", StringComparison.Ordinal) == true,
            "The failure must name Mcp:ToolCallTimeoutSeconds.");
    }

    [Test]
    public void Validate_WhenToolCallTimeoutIsNegative_Fails()
    {
        var result = new McpOptionsValidator().Validate(name: null, new McpOptions
        {
            ToolCallTimeoutSeconds = -1
        });

        AssertEx.True(result.Failed, "A negative ToolCallTimeoutSeconds must fail validation.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Validate_WhenConnectTimeoutIsNotPositive_Fails(int value)
    {
        var result = new McpOptionsValidator().Validate(name: null, new McpOptions
        {
            ConnectTimeoutSeconds = value
        });

        AssertEx.True(result.Failed, "A non-positive ConnectTimeoutSeconds must fail validation.");
        AssertEx.Contains(result.Failures, failure => failure.Contains("ConnectTimeoutSeconds", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenTheLoopbackHostAllowListIsEmpty_Fails()
    {
        // An empty allow-list is not "allow everything" — every HTTP MCP server would be refused, so it is a
        // misconfiguration that must stop startup rather than silently disable the transport.
        var result = new McpOptionsValidator().Validate(name: null, new McpOptions
        {
            HttpLoopbackHosts = []
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.Failures, failure => failure.Contains("HttpLoopbackHosts", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_ReportsEveryViolatedBoundAtOnce()
    {
        var result = new McpOptionsValidator().Validate(name: null, new McpOptions
        {
            ConnectTimeoutSeconds = 0,
            ToolCallTimeoutSeconds = 0,
            HttpLoopbackHosts = []
        });

        AssertEx.True(result.Failed);
        AssertEx.Equal(expected: 3, result.Failures!.Count());
    }
}
