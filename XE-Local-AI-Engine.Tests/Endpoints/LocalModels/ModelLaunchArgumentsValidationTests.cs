namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the refusal contract of the per-model launch-argument override: which rule answers, under which error
///     key, with which message, and — where two rules could both fire — which one wins.
/// </summary>
/// <remarks>
///     Written before those checks moved out of the endpoint into a <c>Validator&lt;T&gt;</c>, and kept afterwards,
///     because a validator reports every failing rule by default and keys its failures by field: either change would
///     be invisible to a status-code assertion and would still break a client reading the first error.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class ModelLaunchArgumentsValidationTests
{
    private const string InvalidModelIdentifier = "Invalid model identifier";

    private const string TooLong = "Launch arguments are too long (max 4096 characters).";

    private const string ReservedModelFlag = "The '-m' argument is managed by the app and cannot be overridden here. Remove it and try again.";

    /// <summary>The general (unkeyed) error field FastEndpoints attaches a message-only failure to.</summary>
    private const string GeneralErrors = "generalErrors";

    [Test]
    public async Task Put_WithAnInvalidModelName_Is400_UnderTheGeneralErrorKey()
    {
        var (status, error) = await PutAsync("bad name", "--top-k 40");

        AssertEx.Equal(HttpStatusCode.BadRequest, status);
        AssertEx.Equal(GeneralErrors, error.Name);
        AssertEx.Equal(InvalidModelIdentifier, error.Reason);
    }

    [Test]
    public async Task Put_WithOverlongArguments_Is400_UnderTheGeneralErrorKey()
    {
        var (status, error) = await PutAsync("llama3:8b", new string('x', count: 4097));

        AssertEx.Equal(HttpStatusCode.BadRequest, status);
        AssertEx.Equal(GeneralErrors, error.Name);
        AssertEx.Equal(TooLong, error.Reason);
    }

    [Test]
    public async Task Put_WithAReservedFlag_Is400_NamingTheFlag()
    {
        var (status, error) = await PutAsync("llama3:8b", "--top-k 40 -m /tmp/other.gguf");

        AssertEx.Equal(HttpStatusCode.BadRequest, status);
        AssertEx.Equal(GeneralErrors, error.Name);
        AssertEx.Equal(ReservedModelFlag, error.Reason);
    }

    /// <summary>
    ///     A request that breaks several rules answers with the FIRST one only, in the order the endpoint applied
    ///     them: model name, then length, then the reserved flag.
    /// </summary>
    /// <remarks>
    ///     This is the assertion a <c>Validator&lt;T&gt;</c> is most likely to break: FluentValidation collects every
    ///     failing rule unless the class-level cascade is set to stop at the first, and the cascade honours
    ///     DECLARATION order, which nothing but this test holds to the handler's. Every case below breaks at least
    ///     two rules — the second pins length-before-reserved, the one boundary that lies between two rules on the
    ///     same property, where a reordering leaves no other trace.
    /// </remarks>
    [Test]
    [Arguments("bad name", InvalidModelIdentifier)]
    [Arguments("llama3:8b", TooLong)]
    public async Task Put_BreakingSeveralRules_ReportsOnlyTheFirst(string modelName, string expected)
    {
        // Overlong AND carrying a reserved flag, so both rules on the arguments field fire at once, and an invalid
        // model name in the first case adds a third. The handler answered with the earliest of them.
        var rawArguments = "-m /tmp/other.gguf " + new string('x', count: 4097);

        var (status, error) = await PutAsync(modelName, rawArguments);

        AssertEx.Equal(HttpStatusCode.BadRequest, status);
        AssertEx.Equal(GeneralErrors, error.Name);
        AssertEx.Equal(expected, error.Reason);
    }

    /// <summary>One refusal, and exactly one: a second entry would change what a client shows the operator.</summary>
    private static async Task<(HttpStatusCode Status, (string? Name, string? Reason) Error)> PutAsync(string modelName, string rawArguments)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/local/v1/models/{Uri.EscapeDataString(modelName)}/launch-args")
        {
            Content = JsonContent.Create(new SetModelLaunchArgumentsRequest { RawArguments = rawArguments })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(content);
        var errors = document.RootElement.GetProperty("errors");

        AssertEx.Equal(expected: 1, errors.GetArrayLength(),
            $"A refusal must carry exactly one error. Body: {content}");

        return (response.StatusCode,
            (errors[0].GetProperty("name").GetString(), errors[0].GetProperty("reason").GetString()));
    }
}
