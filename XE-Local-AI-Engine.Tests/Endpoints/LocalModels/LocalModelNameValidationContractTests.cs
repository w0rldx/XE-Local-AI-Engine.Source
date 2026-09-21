namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the observable refusal every local-model route shares: the model-name check answers 400 with one
///     general-errors entry reading "Invalid model identifier", before the route's service is reached.
/// </summary>
/// <remarks>
///     Eight routes carried a byte-identical copy of that check inside their handler. These assertions are the
///     contract the move into <c>Validators/</c> had to leave untouched — status, error count, error name and reason —
///     and they were green on the unmoved handlers first. The names are checked AFTER route decoding, which is why
///     <c>%2E%2E</c> (a traversal Kestrel decodes into the bound value) is the probe rather than a raw <c>..</c>.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class LocalModelNameValidationContractTests
{
    private const string InvalidModelIdentifier = "Invalid model identifier";

    /// <summary>A name that binds from a route segment and fails the validator's path-traversal guard once decoded.</summary>
    private const string UnsafeRouteName = "%2E%2Esecret";

    [Test]
    [Arguments("DELETE", "/api/local/v1/models/" + UnsafeRouteName)]
    [Arguments("GET", "/api/local/v1/models/" + UnsafeRouteName + "/details")]
    [Arguments("POST", "/api/local/v1/models/" + UnsafeRouteName + "/unload")]
    [Arguments("DELETE", "/api/local/v1/models/" + UnsafeRouteName + "/kind")]
    [Arguments("GET", "/api/local/v1/models/" + UnsafeRouteName + "/launch-args")]
    [Arguments("DELETE", "/api/local/v1/models/" + UnsafeRouteName + "/launch-args")]
    public async Task RouteBoundModelName_WhenItFailsTheNameGrammar_AnswersOneGeneralError(string method, string route)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, new HttpMethod(method), route);
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, InvalidModelIdentifier);
    }

    [Test]
    public async Task SetModelKind_WhenTheNameFailsTheGrammar_AnswersTheNameErrorAndNeverTheKindError()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        // Both checks would fail: the name is unsafe AND the kind is not a defined ModelKind. The name check runs
        // first and short-circuits, so exactly one error comes back and it is the name's.
        using var request = Authorized(factory, HttpMethod.Put, "/api/local/v1/models/" + UnsafeRouteName + "/kind");
        request.Content = JsonContent.Create(new
        {
            kind = "Banana"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, InvalidModelIdentifier);
    }

    [Test]
    public async Task SetModelKind_WhenOnlyTheKindIsUndefined_AnswersTheKindErrorAsAGeneralError()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Put, "/api/local/v1/models/llama3:8b/kind");
        request.Content = JsonContent.Create(new
        {
            kind = "Banana"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "Invalid model kind");
    }

    [Test]
    public async Task SelectLocalModel_WhenTheBodyNameFailsTheGrammar_AnswersOneGeneralError()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, "/api/local/v1/models/select");
        request.Content = JsonContent.Create(new
        {
            modelName = "..secret"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, InvalidModelIdentifier);
    }

    /// <summary>
    ///     A body-bound name is judged RAW: percent-decoding it here would accept a name the handler never decodes.
    /// </summary>
    /// <remarks>
    ///     <c>ext%3Aconn%2Ffoo</c> fails the grammar as written (the allow-pattern has no <c>%</c>) but decodes to the
    ///     well-formed external id <c>ext:conn/foo</c>. The select handler reads <c>req.ModelName</c> unchanged — for
    ///     the external-registration guard and for the write — so a validator that decoded first would pass the
    ///     request through, skip the guard (the raw string has no <c>ext:</c> prefix) and store the mangled name as
    ///     the node default. Decoding belongs to the route-bound routes only.
    /// </remarks>
    [Test]
    public async Task SelectLocalModel_WhenTheBodyNameIsPercentEncoded_RefusesItRawAndWritesNothing()
    {
        var administration = Substitute.For<ILocalModelAdministrationService>();
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILocalModelAdministrationService>();
                services.AddSingleton(administration);
            }
        };
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, "/api/local/v1/models/select");
        request.Content = JsonContent.Create(new
        {
            modelName = "ext%3Aconn%2Ffoo"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, InvalidModelIdentifier);
        await administration.DidNotReceiveWithAnyArgs()
                            .SelectDefaultAsync(Arg.Any<string>(), Arg.Any<LocalModelSelectionPolicy>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The route-bound counterpart: a percent-encoded slash must still be decoded before the grammar runs.
    /// </summary>
    /// <remarks>
    ///     The hey-api path serializer escapes the segment, so a Hugging Face reference arrives carrying a literal
    ///     <c>%2F</c>. The handler decodes it and deletes the decoded name, so the validator must judge that same
    ///     decoded name or every such reference is refused. This is the case the body-bound rule must NOT copy.
    /// </remarks>
    [Test]
    public async Task DeleteLocalModel_WhenTheRouteNameCarriesAnEncodedSlash_IsJudgedAfterDecoding()
    {
        var administration = Substitute.For<ILocalModelAdministrationService>();
        administration.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                      .Returns(callInfo => new LocalModelDeletionResult
                      {
                          Succeeded = true,
                          ModelName = callInfo.ArgAt<string>(0),
                          Deleted = true
                      });
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILocalModelAdministrationService>();
                services.AddSingleton(administration);
            }
        };
        using var client = factory.CreateClient();

        using var request = Authorized(factory,
            HttpMethod.Delete,
            "/api/local/v1/models/hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await administration.Received(requiredNumberOfCalls: 1)
                            .DeleteAsync("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The length rule that already lives in a validator still wins over the name grammar when both would fail.
    /// </summary>
    /// <remarks>
    ///     A 200-character name fails the existing <c>MaximumLength(100)</c> rule AND the grammar's own 150-character
    ///     bound. Today only the first is reported, keyed to the property; a second rule added beside it must not turn
    ///     that into two errors.
    /// </remarks>
    [Test]
    public async Task DeleteLocalModel_WhenTheNameIsOverTheLengthRule_AnswersOnlyThePropertyKeyedLengthError()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Delete, "/api/local/v1/models/" + new string('a', count: 200));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ReadErrorsAsync(response);
        AssertEx.Equal(expected: 1, errors.GetArrayLength());
        AssertEx.Equal("modelName", errors[0].GetProperty("name").GetString());
    }

    private static async Task AssertSingleGeneralErrorAsync(HttpResponseMessage response, string reason)
    {
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ReadErrorsAsync(response);
        AssertEx.Equal(expected: 1, errors.GetArrayLength());
        AssertEx.Equal(FastEndpointsProblemBody.GeneralErrorsName, errors[0].GetProperty("name").GetString());
        AssertEx.Equal(reason, errors[0].GetProperty("reason").GetString());
    }

    private static async Task<JsonElement> ReadErrorsAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("errors").Clone();
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}
