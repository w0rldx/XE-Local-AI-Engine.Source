namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Branch coverage for the durable MCP agent-run bounds. Four of the nine checks are equality pins rather than
///     ranges — the payload and result caps are part of the approved inbound contract, so configuration may not move
///     them in EITHER direction; those cases are asserted both above and below the pinned value so a "raise the limit"
///     edit cannot pass by widening only one side.
/// </summary>
public sealed class McpAgentRunOptionsValidatorTests
{
    private readonly McpAgentRunOptionsValidator _validator = new();

    [Test]
    public void Validate_WithDefaults_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new McpAgentRunOptions());

        AssertEx.False(result.Failed);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(5)]
    public void Validate_WhenMaxConcurrentWorkersIsOutOfRange_ReturnsFailure(int value)
    {
        AssertFailureContains(Validate(new McpAgentRunOptions
            {
                MaxConcurrentWorkers = value
            }),
            "MaxConcurrentWorkers must be between 1 and 4.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(61)]
    public void Validate_WhenWatchdogMinutesIsOutOfRange_ReturnsFailure(int value)
    {
        AssertFailureContains(Validate(new McpAgentRunOptions
            {
                WatchdogMinutes = value
            }),
            "WatchdogMinutes must be between 1 and 60.");
    }

    [Test]
    [Arguments(49)]
    [Arguments(5001)]
    public void Validate_WhenPollIntervalIsOutOfRange_ReturnsFailure(int value)
    {
        AssertFailureContains(Validate(new McpAgentRunOptions
            {
                PollIntervalMilliseconds = value
            }),
            "PollIntervalMilliseconds must be between 50 and 5000.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(61)]
    public void Validate_WhenCompactionIntervalIsOutOfRange_ReturnsFailure(int value)
    {
        AssertFailureContains(Validate(new McpAgentRunOptions
            {
                CompactionIntervalMinutes = value
            }),
            "CompactionIntervalMinutes must be between 1 and 60.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(61)]
    public void Validate_WhenDefaultListLimitIsOutOfRange_ReturnsFailure(int value)
    {
        AssertFailureContains(Validate(new McpAgentRunOptions
            {
                DefaultListLimit = value
            }),
            "DefaultListLimit must be between 1 and 50.");
    }

    [Test]
    [Arguments(16 * 1024)]
    [Arguments(64 * 1024)]
    public void Validate_WhenMaxTaskBytesMovesInEitherDirection_ReturnsFailure(int value)
    {
        AssertFailureContains(Validate(new McpAgentRunOptions
            {
                MaxTaskUtf8Bytes = value
            }),
            "MaxTaskUtf8Bytes must remain 32768.");
    }

    [Test]
    [Arguments(8 * 1024)]
    [Arguments(32 * 1024)]
    public void Validate_WhenMaxInstructionsBytesMovesInEitherDirection_ReturnsFailure(int value)
    {
        AssertFailureContains(Validate(new McpAgentRunOptions
            {
                MaxInstructionsUtf8Bytes = value
            }),
            "MaxInstructionsUtf8Bytes must remain 16384.");
    }

    [Test]
    [Arguments(12_000)]
    [Arguments(48_000)]
    public void Validate_WhenMaxResultCharactersMovesInEitherDirection_ReturnsFailure(int value)
    {
        AssertFailureContains(Validate(new McpAgentRunOptions
            {
                MaxResultCharacters = value
            }),
            "MaxResultCharacters must remain 24000.");
    }

    [Test]
    [Arguments(25)]
    [Arguments(100)]
    public void Validate_WhenMaxListLimitMovesInEitherDirection_ReturnsFailure(int value)
    {
        AssertFailureContains(Validate(new McpAgentRunOptions
            {
                MaxListLimit = value
            }),
            "MaxListLimit must remain 50.");
    }

    [Test]
    public void Validate_ReportsEveryViolatedBoundAtOnce()
    {
        // ValidateOnStart surfaces the whole set, so a misconfigured node is fixed in one pass rather than one restart
        // per bad value.
        var result = _validator.Validate(name: null, new McpAgentRunOptions
        {
            MaxConcurrentWorkers = 9,
            WatchdogMinutes = 0,
            MaxListLimit = 51
        });

        AssertEx.True(result.Failed);
        AssertEx.Equal(expected: 3, result.Failures!.Count());
    }

    private ValidateOptionsResult Validate(McpAgentRunOptions options) =>
        _validator.Validate(name: null, options);

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.True(result.Failed);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}
