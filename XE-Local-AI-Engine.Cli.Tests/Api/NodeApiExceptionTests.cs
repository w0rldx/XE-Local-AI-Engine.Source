namespace XE_Local_AI_Engine.Cli.Tests.Api;

using System.Net;
using System.Text;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class NodeApiExceptionTests
{
    [Test]
    [Arguments("fastendpoints errors[]", 400, """{"title":"One or more validation errors occurred.","status":400,"errors":[{"name":"title","reason":"Title is too long."}]}""", "Title is too long.",
        null)]
    [Arguments("aspnet validation", 400, """{"title":"Validation","errors":{"password":["Password is required."]}}""", "Password is required.", null)]
    [Arguments("conflict envelope", 409, """{"title":"Conflict","detail":"The conversation is read-only.","conflictType":"ReadOnlyConversation"}""", "The conversation is read-only.",
        "ReadOnlyConversation")]
    [Arguments("locked-out body", 401, """{"message":"Try again later.","code":"locked-out","retryAfterSeconds":60}""", "Try again later.", null)]
    [Arguments("title only", 503, """{"title":"Vault locked"}""", "Vault locked", null)]
    [Arguments("empty body", 404, "", "The node answered 404 Not Found.", null)]
    [Arguments("not json", 502, "<html>bad gateway</html>", "The node answered 502 Bad Gateway.", null)]
    [Arguments("json array", 500, "[1,2]", "The node answered 500 Internal Server Error.", null)]
    public async Task FromResponseAsync_ReadsEachErrorBodyShape(string row, int status, string body, string expectedMessage, string? expectedConflictType)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        var exception = await NodeApiException.FromResponseAsync(response, CancellationToken.None);

        AssertEx.Equal((HttpStatusCode)status, exception.StatusCode, row);
        AssertEx.Equal(expectedMessage, exception.Message, row);
        AssertEx.Equal<string?>(expectedConflictType, exception.ConflictType, row);
    }
}
