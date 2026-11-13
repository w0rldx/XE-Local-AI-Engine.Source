namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompatible;

using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The bypass table for operator-supplied header rows. Every later review finding about header input adds a row
///     here. Each row must be refused with a message naming the problem and never echo a value.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class CustomHeaderRulesTests
{
    // A value no refusal may ever contain.
    private const string SecretMarker = "v4lue-must-not-leak";

    [Test]
    [MethodDataSource(nameof(BypassCases))]
    public void FindViolations_WithABypassAttempt_RefusesWithoutEchoingTheValue(BypassCase bypass)
    {
        var message = string.Join(" ", CustomHeaderRules.FindViolations(bypass.Headers));

        AssertEx.Contains(message, bypass.ExpectedMessageFragment, StringComparison.Ordinal, bypass.Label);
        AssertEx.False(message.Contains(SecretMarker, StringComparison.Ordinal), $"{bypass.Label}: the refusal must not echo a value.");
    }

    [Test]
    public void FindViolations_WithValidRowsAndAnEmptyEditorRow_ReportsNothing()
    {
        AssertEx.Empty(CustomHeaderRules.FindViolations([Row(" X-Example ", " demo "), Row("", ""), Row("X-Empty", "")]));
    }

    public static IEnumerable<Func<BypassCase>> BypassCases()
    {
        foreach (var reserved in new[]
                 {
                     "Authorization",
                     "authorization",
                     "AUTHORIZATION",
                     "aUtHoRiZaTiOn",
                     " Authorization ",
                     "api-key",
                     "API-KEY",
                     "Api-Key",
                     "host",
                     "HOST",
                     "cookie",
                     "COOKIE",
                     "proxy-authorization",
                     "Proxy-Authorization",
                     "content-type",
                     "Content-Length",
                     "CONTENT-ENCODING",
                     "transfer-encoding",
                     "Connection",
                     "expect",
                     // Content headers .NET refuses on a request; accepted, they threw on every send of the connection.
                     "Allow",
                     "content-disposition",
                     "Content-Language",
                     "CONTENT-LOCATION",
                     "Content-MD5",
                     "content-range",
                     "Expires",
                     "last-modified"
                 })
        {
            yield return () => new BypassCase($"reserved name '{reserved}'", [Row(reserved, SecretMarker)], "is reserved");
        }

        yield return () => new BypassCase("CR LF in a value", [Row("X-Example", SecretMarker + "\r\nX-Injected: 1")], "invalid control characters");
        yield return () => new BypassCase("LF in a value", [Row("X-Example", SecretMarker + "\nX-Injected: 1")], "invalid control characters");
        yield return () => new BypassCase("NUL in a value", [Row("X-Example", SecretMarker + "\0")], "invalid control characters");
        yield return () => new BypassCase("non-ASCII in a value", [Row("X-Example", SecretMarker + "-café")], "non-ASCII characters");
        yield return () => new BypassCase("non-BMP in a value", [Row("X-Example", SecretMarker + "-😀")], "non-ASCII characters");
        yield return () => new BypassCase("DEL in a value", [Row("X-Example", SecretMarker + "\u007F")], "invalid control characters");
        yield return () => new BypassCase("CR LF in a name", [Row("X-Example\r\nX-Injected", SecretMarker)], "invalid characters");
        yield return () => new BypassCase("NUL in a name", [Row("X-Exa\0mple", SecretMarker)], "invalid characters");
        yield return () => new BypassCase("colon in a name", [Row("X-Example:", SecretMarker)], "invalid characters");
        yield return () => new BypassCase("over-length name", [Row(new string('a', CustomHeaderRules.MaxHeaderNameLength + 1), SecretMarker)], "exceeds");
        yield return () => new BypassCase("over-length value", [Row("X-Example", SecretMarker + new string('v', CustomHeaderRules.MaxHeaderValueLength))], "value exceeds");
        yield return () => new BypassCase("too many headers",
            [.. Enumerable.Range(0, CustomHeaderRules.MaxHeaderCount + 1).Select(static index => Row($"X-H{index}", SecretMarker))],
            "A maximum of");
        yield return () => new BypassCase("duplicate names by case", [Row("X-Example", SecretMarker), Row("x-EXAMPLE", SecretMarker)], "is duplicated");
        yield return () => new BypassCase("duplicate names by padding", [Row("X-Example", SecretMarker), Row(" X-Example", SecretMarker)], "is duplicated");
        yield return () => new BypassCase("blank name with a value", [Row("", SecretMarker)], "without a header name");
        yield return () => new BypassCase("whitespace name with a value", [Row("   ", SecretMarker)], "without a header name");
    }

    private static KeyValuePair<string?, string?> Row(string name, string value)
    {
        return new KeyValuePair<string?, string?>(name, value);
    }

    /// <summary>One row of the bypass table.</summary>
    public sealed class BypassCase
    {
        public BypassCase(string label, IReadOnlyCollection<KeyValuePair<string?, string?>> headers, string expectedMessageFragment)
        {
            Label = label;
            Headers = headers;
            ExpectedMessageFragment = expectedMessageFragment;
        }

        public string Label { get; }

        public IReadOnlyCollection<KeyValuePair<string?, string?>> Headers { get; }

        public string ExpectedMessageFragment { get; }

        public override string ToString()
        {
            return Label;
        }
    }
}
