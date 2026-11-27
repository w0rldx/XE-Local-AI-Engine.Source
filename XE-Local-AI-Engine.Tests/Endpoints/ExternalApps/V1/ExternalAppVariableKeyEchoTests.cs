namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalApps.V1;

using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Validators;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>A refused variable key is echoed cut to 64 characters, never more and never half of a surrogate pair.</summary>
[Category(TestCategories.Unit)]
public sealed class ExternalAppVariableKeyEchoTests
{
    [Test]
    public void AnOverlongKey_IsEchoedCutTo64Characters()
    {
        AssertEx.Equal($"Variable '{new string('A', count: 64)}' exceeds 64 characters.", OverlongMessage(new string('A', count: 100)));
    }

    [Test]
    public void AnOverlongKey_WhoseCutSplitsASurrogatePair_IsEchoedWithoutThePair()
    {
        AssertEx.Equal($"Variable '{new string('A', count: 63)}' exceeds 64 characters.",
            OverlongMessage(new string('A', count: 63) + "\U0001F600" + new string('B', count: 10)));
    }

    private static string OverlongMessage(string key)
    {
        var result = new UpdateExternalAppVariablesRequestValidator().Validate(new UpdateExternalAppVariablesRequest
        {
            Variables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [key] = "value"
            },
            ExpectedVersion = 1
        });

        AssertEx.False(result.Errors.Any(static error => error.ErrorMessage.Any(char.IsSurrogate)), "half of a surrogate pair was echoed");
        return result.Errors.Single(static error => error.ErrorMessage.Contains("exceeds 64", StringComparison.Ordinal)).ErrorMessage;
    }
}
