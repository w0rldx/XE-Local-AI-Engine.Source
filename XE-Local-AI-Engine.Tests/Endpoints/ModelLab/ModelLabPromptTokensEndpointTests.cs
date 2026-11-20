namespace XE_Local_AI_Engine.Tests.Endpoints.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>POST <c>model-lab/prompt-tokens</c>: operator only, the run's size cap, a model no longer running is a 409, and the tokens map through.</summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabPromptTokensEndpointTests
{
    private const string Route = "/api/local/v1/model-lab/prompt-tokens";
    private const string Model = "org/Some-GGUF:Q4_K_M";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Anonymous_Is401()
    {
        var service = Substitute.For<IModelLabPromptTokensService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new
            {
                modelName = Model,
                prompt = "hi"
            })
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ANonOperator_Is403()
    {
        var service = Substitute.For<IModelLabPromptTokensService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new
            {
                modelName = Model,
                prompt = "hi"
            })
        };
        factory.AddNonOperatorBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await service.DidNotReceiveWithAnyArgs().GetPromptTokensAsync(null!, CancellationToken.None);
    }

    [Test]
    [Arguments("", "hi")]
    [Arguments("not a model name!", "hi")]
    [Arguments(Model, "")]
    [Arguments(Model, "   ")]
    public async Task AMalformedRequest_Is400(string modelName, string prompt)
    {
        var service = Substitute.For<IModelLabPromptTokensService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, new
        {
            modelName,
            prompt
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await service.DidNotReceiveWithAnyArgs().GetPromptTokensAsync(null!, CancellationToken.None);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task APromptOverTheMessageCap_Is400(bool inTheSystemPrompt)
    {
        var service = Substitute.For<IModelLabPromptTokensService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();
        var oversized = new string('a', (factory.Services.GetRequiredService<IOptions<SecurityOptions>>().Value.MaxMessageSizeKb * 1024) + 1);

        using var request = Authorized(factory, new
        {
            modelName = Model,
            prompt = inTheSystemPrompt ? "hi" : oversized,
            systemPrompt = inTheSystemPrompt ? oversized : null
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await service.DidNotReceiveWithAnyArgs().GetPromptTokensAsync(null!, CancellationToken.None);
    }

    [Test]
    public async Task AModelNoLongerRunning_Is409WithItsCode()
    {
        var service = Substitute.For<IModelLabPromptTokensService>();
        service.GetPromptTokensAsync(Arg.Any<ModelLabPromptTokensQuery>(), Arg.Any<CancellationToken>())
               .Returns((ModelLabPromptTokens?)null);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, new
        {
            modelName = Model,
            prompt = "hi"
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(nameof(ModelLabStartRefusal.ModelNotLoaded), body.RootElement.GetProperty("reason").GetString());
    }

    [Test]
    public async Task TheRunsInputs_ReachTheService_AndItsTokensMapThrough()
    {
        var service = Substitute.For<IModelLabPromptTokensService>();
        service.GetPromptTokensAsync(Arg.Any<ModelLabPromptTokensQuery>(), Arg.Any<CancellationToken>())
               .Returns(new ModelLabPromptTokens
               {
                   RenderedPrompt = "<|im_start|>user\nhi",
                   Tokens =
                   [
                       new ModelLabPromptToken
                       {
                           Id = 151644,
                           Piece = "<|im_start|>",
                           Special = true
                       },
                       new ModelLabPromptToken
                       {
                           Id = 872,
                           Piece = "user",
                           Special = false
                       }
                   ],
                   TokenCount = 5,
                   Truncated = true
               });
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, new
        {
            modelName = Model,
            systemPrompt = "be brief",
            prompt = "hi",
            disableThinking = true
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ModelLabPromptTokensResponse>(JsonOptions));
        AssertEx.Equal("<|im_start|>user\nhi", body.RenderedPrompt);
        AssertEx.Equal(5, body.TokenCount);
        AssertEx.True(body.Truncated);
        AssertEx.Equal(2, body.Tokens.Count);
        AssertEx.Equal(151644, body.Tokens[0].Id);
        AssertEx.Equal("<|im_start|>", body.Tokens[0].Piece);
        AssertEx.True(body.Tokens[0].Special);
        AssertEx.False(body.Tokens[1].Special);
        await service.Received(1).GetPromptTokensAsync(Arg.Is<ModelLabPromptTokensQuery>(static query => query.ModelName == Model
                                                                                                         && query.SystemPrompt == "be brief"
                                                                                                         && query.Prompt == "hi"
                                                                                                         && query.DisableThinking),
            Arg.Any<CancellationToken>());
    }

    private static TestServerWebAppFactory CreateFactory(IModelLabPromptTokensService service) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IModelLabPromptTokensService>();
                services.AddSingleton(service);
            }
        };

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(body)
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}
