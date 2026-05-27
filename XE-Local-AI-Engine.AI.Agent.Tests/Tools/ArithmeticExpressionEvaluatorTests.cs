namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ArithmeticExpressionEvaluatorTests
{
    [Test]
    [Arguments("12 * 9", 108d)]
    [Arguments("2 + 3 * 4", 14d)]
    [Arguments("(2 + 3) * 4", 20d)]
    [Arguments("10 / 4", 2.5d)]
    [Arguments("-5 + 2", -3d)]
    [Arguments("-(3 + 4)", -7d)]
    [Arguments("  7  -  2  ", 5d)]
    [Arguments("3.5 + 1.5", 5d)]
    public void TryEvaluate_ComputesArithmetic(string expression, double expected)
    {
        var success = ArithmeticExpressionEvaluator.TryEvaluate(expression, out var result);

        AssertEx.True(success, $"Expected '{expression}' to evaluate.");
        AssertEx.True(Math.Abs(result - expected) < 1e-9, $"Expected {expected} but got {result} for '{expression}'.");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("drop table users")]
    [Arguments("System.Environment.Exit(0)")]
    [Arguments("2 +")]
    [Arguments("(2 + 3")]
    [Arguments("2 ** 3")]
    [Arguments("5 / 0")]
    [Arguments("abc")]
    [Arguments("2 + 3 = 5")]
    public void TryEvaluate_RejectsNonArithmeticOrMalformed(string expression)
    {
        var success = ArithmeticExpressionEvaluator.TryEvaluate(expression, out var result);

        AssertEx.False(success, $"Expected '{expression}' to be rejected.");
        AssertEx.Equal(0d, result);
    }

    [Test]
    public void TryEvaluate_NullExpression_ReturnsFalse()
    {
        var success = ArithmeticExpressionEvaluator.TryEvaluate(null, out var result);

        AssertEx.False(success);
        AssertEx.Equal(0d, result);
    }
}
