namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using System.Text;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Pins the gateway shapes <see cref="ExternalProviderErrorDetail" /> recognizes and that a sent secret still voids them.</summary>
[Category(TestCategories.Unit)]
public sealed class ExternalProviderErrorDetailTests
{
    private const string PrincipalId = "f3a9c2d18b7e4c06";

    private const string RbacBody =
        $$$"""{"error":{"code":"PermissionDenied","message":"The principal `{{{PrincipalId}}}` lacks the required data action `Microsoft.CognitiveServices/accounts/OpenAI/responses/write` to perform `POST /openai/v1/responses` operation."}}""";

    [Test]
    public void Parse_PermissionDenied_KeepsTheMixedCaseCodeAndDropsThePathBearingMessage()
    {
        var detail = ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes(RbacBody));

        AssertEx.Equal<string?>(ExternalProviderErrorDetail.PermissionDeniedCode, detail.Text);
    }

    // The principal id equal to a sent secret voids the whole detail, the known code included.
    [Test]
    public void Parse_PermissionDeniedEchoingASentSecret_VoidsTheWholeDetail()
    {
        var detail = ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes(RbacBody), [PrincipalId]);

        AssertEx.Null(detail.Text);
    }

    // Only the exact known spelling survives; any other mixed-case code is still dropped.
    [Test]
    [Arguments("permissiondenied", "permissiondenied")]
    [Arguments("PermissionDeniedX", null)]
    [Arguments("SomeOtherCode", null)]
    public void Parse_MixedCaseCode_SurvivesOnlyAsTheKnownSpelling(string code, string? expected)
    {
        var detail = ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes($$$"""{"error":{"code":"{{{code}}}"}}"""));

        AssertEx.Equal<string?>(expected, detail.Code);
    }

    [Test]
    [Arguments("""{"error":{"code":"apim_policy_error","message":"Unable to match incoming request to an operation."}}""", true)]
    [Arguments("""{"message":"unable to match incoming request to an operation"}""", true)]
    [Arguments("""{"error":{"code":"apim_policy_error","message":"Policy expression evaluation failed."}}""", false)]
    [Arguments("""{"error":"unsupported_tpm_configuration","message":"TokensPerMinute must be one of 10000, 30000."}""", false)]
    public void ReportsNoRoute_MatchesOnlyTheNoOperationMessage(string body, bool expected)
    {
        AssertEx.Equal(expected, ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes(body)).ReportsNoRoute);
    }

    [Test]
    [Arguments("""{"error":{"message":"The requested operation is unsupported."}}""", true)]
    [Arguments("""{"message":"the requested operation is unsupported"}""", true)]
    [Arguments("""{"error":{"code":"api_not_supported","message":"Requested API is currently not supported"}}""", false)]
    public void ReportsUnsupportedOperation_MatchesOnlyTheUnsupportedOperationMessage(string body, bool expected)
    {
        AssertEx.Equal(expected, ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes(body)).ReportsUnsupportedOperation);
    }
}
