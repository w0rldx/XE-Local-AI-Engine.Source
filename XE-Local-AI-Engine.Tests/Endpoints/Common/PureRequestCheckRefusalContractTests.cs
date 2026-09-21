namespace XE_Local_AI_Engine.Tests.Endpoints.Common;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the observable refusal of every handler-side request-shape check that is a candidate for a validator:
///     status, error count, error name and reason, and which error wins when two checks would fail together.
/// </summary>
/// <remarks>
///     Each check below is an unkeyed <c>AddError(message)</c> followed by <c>Send.ErrorsAsync()</c>, so it lands under
///     the general-errors field. These assertions were green on the unmoved handlers first: a move into
///     <c>V1/Validators/</c> must leave every one of them untouched. The pre-emption cases matter most — a validator
///     runs before the handler, so a request that is both malformed and unknown must still answer the same way.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class PureRequestCheckRefusalContractTests
{
    private const string ApiPrefix = "/api/local/v1";

    /// <summary>A provenance model name one character over <c>GenerationProvenance.MaxModelLength</c>.</summary>
    private static readonly string OversizedProvenanceModel = new('m', count: 201);

    private const string OversizedProvenanceMessage = "Generation metadata model must be at most 200 characters.";

    [Test]
    [Arguments("POST", ApiPrefix + "/skills")]
    [Arguments("POST", ApiPrefix + "/agents")]
    public async Task CreateWithAnOversizedProvenanceBlock_AnswersOneGeneralError(string method, string route)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, new HttpMethod(method), route, ProvenanceBody());
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, OversizedProvenanceMessage);
    }

    /// <summary>The provenance cap is refused before the record is looked up, so an unknown id still answers 400.</summary>
    /// <remarks>
    ///     This is the pre-emption pin for the two update routes: their 404 is written by the handler AFTER the
    ///     provenance check, so moving that check into a validator must not turn this request into a 404.
    /// </remarks>
    [Test]
    [Arguments("PUT", ApiPrefix + "/skills/")]
    [Arguments("PUT", ApiPrefix + "/agents/")]
    public async Task UpdateWithAnOversizedProvenanceBlock_AnswersTheShapeErrorAndNotTheUnknownId(string method, string routePrefix)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, new HttpMethod(method), routePrefix + Guid.NewGuid(), ProvenanceBody());
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, OversizedProvenanceMessage);
    }

    /// <summary>The draft boundary reports only its first violation, in the order its own checks are written.</summary>
    [Test]
    [Arguments(ApiPrefix + "/skills/draft")]
    [Arguments(ApiPrefix + "/agents/draft")]
    public async Task Draft_WhenModelAndBriefAreBothMissing_ReportsOnlyTheModel(string route)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, route, new
        {
            mode = "create"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "A model is required.");
    }

    [Test]
    [Arguments(ApiPrefix + "/skills/draft")]
    [Arguments(ApiPrefix + "/agents/draft")]
    public async Task Draft_WhenOnlyTheBriefIsMissing_ReportsTheBrief(string route)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, route, new
        {
            mode = "create",
            modelName = "llama3:8b"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "A description of what you want is required.");
    }

    /// <summary>The image-job checks run model, then prompt, then seed, and stop at the first.</summary>
    [Test]
    public async Task CreateImageJob_WhenEveryCheckWouldFail_ReportsOnlyTheModelName()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/jobs", new
        {
            modelName = " ",
            prompt = " ",
            seed = "not-a-number"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "A model name is required.");
    }

    [Test]
    public async Task CreateImageJob_WhenOnlyTheSeedIsUnparseable_ReportsTheSeed()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/jobs", new
        {
            modelName = "sd-1.5",
            prompt = "a cat",
            seed = "not-a-number"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, SeedValue.ValidationMessage);
    }

    [Test]
    public async Task DeleteImageModel_WhenTheRouteNameIsBlank_AnswersOneGeneralError()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/images/models/%20%20");
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "A model name is required.");
    }

    /// <summary>
    ///     The blankness check judges the RAW bound name while the store is handed the trimmed one.
    /// </summary>
    /// <remarks>
    ///     A padded but non-blank name must pass and reach the store trimmed. A rule written against the trimmed value
    ///     would agree here, but a rule that trimmed and then re-checked blankness would not; this pins which string is
    ///     judged and which is used.
    /// </remarks>
    [Test]
    public async Task DeleteImageModel_WhenTheRouteNameIsPadded_PassesAndReachesTheStoreTrimmed()
    {
        var store = Substitute.For<IImageModelStore>();
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IImageModelStore>();
                services.AddSingleton(store);
            }
        };
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/images/models/%20%20sd-1.5%20%20");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await store.Received(requiredNumberOfCalls: 1).DeleteModelAsync("sd-1.5", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InspectImageRepository_WhenTheRepoIdIsBlank_AnswersOneGeneralError()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/images/models/inspect?repoId=%20");
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "A repository id is required.");
    }

    /// <summary>The wire shape is judged before the file-set shape, so a blank name beats a roleless set.</summary>
    [Test]
    public async Task StartImageModelDownload_WhenTheNameAndTheFileSetAreBothWrong_ReportsOnlyTheName()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads", new
        {
            modelName = " ",
            repoId = "org/repo",
            family = "Flux",
            parts = Array.Empty<object>()
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "A model name is required.");
    }

    [Test]
    public async Task StartImageModelDownload_WhenTheFileSetHasNoDiffusionPart_ReportsTheFileSet()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads", new
        {
            modelName = "flux-dev",
            repoId = "org/repo",
            family = "Flux",
            parts = new[]
            {
                new
                {
                    role = "Vae",
                    fileName = "ae.safetensors"
                }
            }
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "The file-set must include a diffusion part.");
    }

    /// <summary>
    ///     The paging bounds are refused before the project is read, so an unknown project still answers 400.
    /// </summary>
    /// <remarks>
    ///     The 404 this route would otherwise answer is written through <c>BenchmarkEndpointSupport</c>, a different
    ///     body shape entirely; the paging refusal must keep winning.
    /// </remarks>
    [Test]
    public async Task ListBenchmarkRuns_WhenThePageIsOutOfBounds_AnswersTheShapeErrorAndNotTheUnknownProject()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory,
            HttpMethod.Get,
            $"{ApiPrefix}/benchmarks/projects/{Guid.NewGuid()}/runs?page=0&pageSize=50");
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "Page must be positive and pageSize must be between 1 and 200.");
    }

    /// <summary>
    ///     The blank-model refusal wins over the KV-cache refusal, which answers a different body shape.
    /// </summary>
    [Test]
    public async Task StartBenchmarkRun_WhenTheModelAndTheKvCacheTypeAreBothWrong_ReportsOnlyTheModel()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory,
            HttpMethod.Post,
            $"{ApiPrefix}/benchmarks/projects/{Guid.NewGuid()}/runs",
            new
            {
                modelName = " ",
                kvCacheType = "q3_banana"
            });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "A primary model is required.");
    }

    [Test]
    public async Task ListEligibleBenchmarkAgents_WhenTheModelNameIsBlank_AnswersOneGeneralError()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/benchmarks/eligible-agents?modelName=%20");
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "A model name is required.");
    }

    /// <summary>The query is judged before the collection id, so a blank query beats an unusable collection.</summary>
    [Test]
    public async Task SearchKnowledge_WhenTheQueryAndTheCollectionAreBothWrong_ReportsOnlyTheQuery()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/knowledge-base/search", new
        {
            query = " ",
            collectionId = "not a collection!"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "A search query is required.");
    }

    [Test]
    public async Task SearchKnowledge_WhenTheQueryIsOverTheContentBound_ReportsTheLength()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/knowledge-base/search", new
        {
            query = new string('q', KnowledgeQueryLimits.MaxQueryLength + 1)
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response,
            $"The search query must be {KnowledgeQueryLimits.MaxQueryLength} characters or fewer.");
    }

    [Test]
    public async Task SearchKnowledge_WhenOnlyTheCollectionIsUnusable_ReportsTheCollection()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/knowledge-base/search", new
        {
            query = "embeddings",
            collectionId = "not a collection!"
        });
        using var response = await client.SendAsync(request);

        await AssertSingleGeneralErrorAsync(response, "The collection id is invalid.");
    }

    private static object ProvenanceBody()
    {
        return new
        {
            name = "Shape Contract",
            description = "d",
            body = "b",
            instructions = "i",
            generationMetadata = new
            {
                mode = "create",
                model = OversizedProvenanceModel,
                confidence = 0.5
            }
        };
    }

    private static async Task AssertSingleGeneralErrorAsync(HttpResponseMessage response, string reason)
    {
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var errors = document.RootElement.GetProperty("errors");
        AssertEx.Equal(expected: 1, errors.GetArrayLength());
        AssertEx.Equal(FastEndpointsProblemBody.GeneralErrorsName, errors[0].GetProperty("name").GetString());
        AssertEx.Equal(reason, errors[0].GetProperty("reason").GetString());
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory,
        HttpMethod method,
        string route,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, route);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}
