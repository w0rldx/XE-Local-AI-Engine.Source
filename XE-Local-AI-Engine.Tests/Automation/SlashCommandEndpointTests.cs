namespace XE_Local_AI_Engine.Tests.Automation;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Automation.V1;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Automation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SlashCommandEndpointTests
{
    private const string Route = "/api/local/v1/automation/commands";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Test]
    public async Task ListCommands_WithoutOperatorAuthentication_ReturnsUnauthorized()
    {
        var service = Substitute.For<ISlashCommandService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Route);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await service.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListCommands_WhenAuthorized_ReturnsReadOnlyBuiltinPing()
    {
        var service = Substitute.For<ISlashCommandService>();
        service.ListAsync(Arg.Any<CancellationToken>()).Returns([
            new SlashCommandCatalogItem(null, "ping", "Test", "builtIn", SlashCommandActionType.SendPrompt, "Respond with exactly PONG and nothing else.")
        ]);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, Route);
        using var response = await client.SendAsync(request);
        var body = await ReadJsonAsync<ListSlashCommandsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 1, body.Items.Count);
        AssertEx.Null(body.Items[0].Id);
        AssertEx.Equal("builtIn", body.Items[0].Source);
        AssertEx.Equal(SlashCommandActionTypeDto.SendPrompt, body.Items[0].Action.Type);
    }

    [Test]
    public async Task CreateCommand_WhenAuthorized_ReturnsCreatedTypedAction()
    {
        var service = Substitute.For<ISlashCommandService>();
        var id = Guid.NewGuid();
        service.CreateAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>())
               .Returns(new SlashCommandCatalogItem(id, "review", null, "custom", SlashCommandActionType.SendPrompt, "Review this."));
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, Route);
        request.Content = JsonContent.Create(new
        {
            name = "review",
            action = new
            {
                type = "sendPrompt",
                prompt = "Review this."
            }
        });
        using var response = await client.SendAsync(request);
        var body = await ReadJsonAsync<SlashCommandResponse>(response);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertEx.Equal(id, body.Id);
        AssertEx.Equal(SlashCommandActionTypeDto.SendPrompt, body.Action.Type);
    }

    [Test]
    [Arguments("{\"action\":{\"type\":\"sendPrompt\",\"prompt\":\"Review this.\"}}")]
    [Arguments("{\"name\":\"review\"}")]
    [Arguments("{\"name\":\"review\",\"action\":{\"prompt\":\"Review this.\"}}")]
    [Arguments("{\"name\":\"review\",\"action\":{\"type\":\"sendPrompt\"}}")]
    public async Task CreateCommand_WhenRequiredTypedFieldIsMissing_ReturnsBadRequest(string json)
    {
        var service = Substitute.For<ISlashCommandService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, Route);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemResponseAsync(response, HttpStatusCode.BadRequest);
        await service.DidNotReceive().CreateAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateCommand_WithExactSendPromptDiscriminator_ReachesService()
    {
        var service = Substitute.For<ISlashCommandService>();
        service.CreateAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>())
               .Returns(new SlashCommandCatalogItem(Guid.NewGuid(), "review", null, "custom", SlashCommandActionType.SendPrompt, "Review this."));
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, Route);
        request.Content = new StringContent("{\"name\":\"review\",\"action\":{\"type\":\"sendPrompt\",\"prompt\":\"Review this.\"}}",
            Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode);
        await service.Received(1).CreateAsync(Arg.Is<SlashCommandInput>(input => input.ActionType == SlashCommandActionType.SendPrompt), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("1")]
    [Arguments("\"SendPrompt\"")]
    [Arguments("\"unknown\"")]
    public async Task CreateCommand_WithNonLiteralDiscriminator_ReturnsBadRequest(string discriminatorJson)
    {
        var service = Substitute.For<ISlashCommandService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, Route);
        request.Content = new StringContent($"{{\"name\":\"review\",\"action\":{{\"type\":{discriminatorJson},\"prompt\":\"Review this.\"}}}}",
            Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemResponseAsync(response, HttpStatusCode.BadRequest);
        await service.DidNotReceive().CreateAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCommand_WhenMissing_ReturnsNotFound()
    {
        var service = Substitute.For<ISlashCommandService>();
        var id = Guid.NewGuid();
        service.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((SlashCommandCatalogItem?)null);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, $"{Route}/{id}");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Equal(expected: 0L, response.Content.Headers.ContentLength ?? 0L);
        AssertEx.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task UpdateCommand_WhenDuplicate_ReturnsConflict()
    {
        var service = Substitute.For<ISlashCommandService>();
        service.UpdateAsync(Arg.Any<Guid>(), Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>())
               .Returns<SlashCommandCatalogItem?>(_ => throw new SlashCommandConflictException("duplicate"));
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        using var request = CreateRequest(factory, HttpMethod.Put, $"{Route}/{id}");
        request.Content = JsonContent.Create(new
        {
            name = "review",
            action = new
            {
                type = "sendPrompt",
                prompt = "Review this."
            }
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemResponseAsync(response, HttpStatusCode.Conflict);
    }

    [Test]
    public async Task DeleteCommand_WhenDeleted_ReturnsNoContent()
    {
        var service = Substitute.For<ISlashCommandService>();
        service.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        using var request = CreateRequest(factory, HttpMethod.Delete, $"{Route}/{id}");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static TestServerWebAppFactory CreateFactory(ISlashCommandService service) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ISlashCommandService>();
                services.AddScoped(_ => service);
            }
        };

    private static HttpRequestMessage CreateRequest(TestServerWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response) where T : class
    {
        var body = await response.Content.ReadAsStringAsync();
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(body, JsonOptions));
    }

    private static async Task AssertProblemResponseAsync(HttpResponseMessage response, HttpStatusCode statusCode)
    {
        AssertEx.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal((int)statusCode, document.RootElement.GetProperty("status").GetInt32());
        AssertEx.True(document.RootElement.TryGetProperty("title", out _), "Problem details must carry a title.");
    }
}
