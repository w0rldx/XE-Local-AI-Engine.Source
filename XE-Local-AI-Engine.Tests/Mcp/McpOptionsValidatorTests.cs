namespace XE_Local_AI_Engine.Tests.Mcp;

using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     AUD4-18: the MCP options validator rejects a non-positive per-call tool timeout (alongside the existing connect
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
}
