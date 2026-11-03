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

    // A gateway's hyphenated deny reason is a code; a hyphen must not open the door to a key or an id, so digits after one still drop it.
    [Test]
    [Arguments("not-a-member", "not-a-member")]
    [Arguments("project-archived", "project-archived")]
    [Arguments("model-not-approved", "model-not-approved")]
    [Arguments("tenant-mismatch", "tenant-mismatch")]
    [Arguments("sk-abc123def456", null)]
    [Arguments("sk-live-abc", null)]
    [Arguments("proj-000001", null)]
    [Arguments("a-b/c", null)]
    [Arguments("-leading", null)]
    [Arguments("trailing-", null)]
    [Arguments("double--hyphen", null)]
    [Arguments("https://gateway.example/x", null)]
    [Arguments("this-code-is-far-too-long-to-be-any-reasonable-code", null)]
    public void Parse_HyphenatedCode_SurvivesOnlyAsPlainWords(string code, string? expected)
    {
        var detail = ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes($$$"""{"error":"{{{code}}}"}"""));

        AssertEx.Equal<string?>(expected, detail.Code);
    }

    [Test]
    public void Parse_MessageWithAnApostrophe_IsKept()
    {
        var detail = ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes("""{"error":"tenant-mismatch","message":"The token's tenant is not served by this gateway."}"""));

        AssertEx.Equal<string?>("tenant-mismatch: The token's tenant is not served by this gateway.", detail.Text);
    }

    // The grammar's whole purpose: one unsafe word drops the message, apostrophes and all.
    [Test]
    [Arguments("See https://gateway.example/help for details.")]
    [Arguments("The key sk-abc123def456 was refused.")]
    [Arguments("Project proj-000001 is archived.")]
    [Arguments("The token's=abc tenant is wrong.")]
    [Arguments("It's a <script>alert(1)</script> tag.")]
    [Arguments("Look in /etc/secrets for it's value.")]
    [Arguments("Owner's id 3f2a9c81 is refused.")]
    public void Parse_HostileMessage_IsDroppedWhole(string message)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { message });

        AssertEx.Null(ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes(body)).Text);
    }

    [Test]
    public void Parse_MessageAndReason_KeepsBoth()
    {
        var detail = ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes(
            """{"error":"token_quota_exceeded","message":"Token limit is exceeded. Try again in 3540 seconds.","reason":"project-quota"}"""));

        AssertEx.Equal<string?>("token_quota_exceeded: Token limit is exceeded. Try again in 3540 seconds. (reason: project-quota)", detail.Text);
    }

    [Test]
    [Arguments("""{"message":"Denied.","reason":"https://gateway.example/why"}""", "Denied.")]
    [Arguments("""{"message":"Denied.","reason":"Denied."}""", "Denied.")]
    [Arguments("""{"reason":"Model is not assigned to this project."}""", "Model is not assigned to this project.")]
    [Arguments("""{"error":"project-quota","reason":"project-quota"}""", "project-quota")]
    public void Parse_Reason_IsSanitizedAndNeverRepeated(string body, string expected)
    {
        AssertEx.Equal<string?>(expected, ExternalProviderErrorDetail.Parse(Encoding.UTF8.GetBytes(body)).Text);
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
