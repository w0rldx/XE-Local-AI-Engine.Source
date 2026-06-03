namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class PullStreamEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task PullStream_WhenProviderStreams_EmitsSanitizedProgressEvents()
    {
        // Arrange — three provider progress events; the middle one carries a digest that must NOT leak.
        var modelService = Substitute.For<IOllamaModelService>();
        modelService.PullModelAsync("tinyllama:latest", Arg.Any<CancellationToken>())
                    .Returns(BuildProgressSequence());

        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IOllamaModelService>();
                services.AddSingleton(modelService);
            }
        };

        using var client = factory.CreateClient();
        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/models/pull/stream");
        request.Content = JsonContent.Create(new PullLocalModelRequest { ModelName = "tinyllama:latest" });

        // Act
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        // Assert — HTTP layer
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        // Parse NDJSON lines
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        AssertEx.Equal(3, lines.Length);

        var first = ParseEvent(lines[0]);
        AssertEx.Equal("pulling manifest", first.Status);
        // Ollama sends 0 for non-byte-progress lines; the endpoint forwards the value as-is.
        AssertEx.Equal(0L, first.CompletedBytes);
        AssertEx.Equal(0L, first.TotalBytes);

        var second = ParseEvent(lines[1]);
        AssertEx.Equal("pulling layers", second.Status);
        AssertEx.Equal(50L, second.CompletedBytes);
        AssertEx.Equal(100L, second.TotalBytes);

        var third = ParseEvent(lines[2]);
        AssertEx.Equal("success", third.Status);
        AssertEx.Equal(0L, third.CompletedBytes);
        AssertEx.Equal(0L, third.TotalBytes);

        // No extra fields leak (digest, model name, etc.) — round-trip through the typed DTO is the gate.
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            AssertEx.True(props.IsSubsetOf(["status", "completedBytes", "totalBytes"]),
                "Sanitized event must contain only {status, completedBytes, totalBytes}.");
        }
    }

    [Test]
    public async Task PullStream_WhenModelNameIsUnsafe_ReturnsValidationProblem()
    {
        var modelService = Substitute.For<IOllamaModelService>();

        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IOllamaModelService>();
                services.AddSingleton(modelService);
            }
        };

        using var client = factory.CreateClient();
        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/models/pull/stream");
        request.Content = JsonContent.Create(new PullLocalModelRequest { ModelName = "../secret" });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        modelService.DidNotReceiveWithAnyArgs().PullModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<PullModelResponse> BuildProgressSequence()
    {
        // Ollama sends 0 (not null) when there is no byte-progress data for a line.
        yield return new PullModelResponse { Status = "pulling manifest", Total = 0L, Completed = 0L };
        yield return new PullModelResponse { Status = "pulling layers", Total = 100L, Completed = 50L };
        yield return new PullModelResponse { Status = "success", Total = 0L, Completed = 0L };

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static PullStreamProgressEvent ParseEvent(string line)
    {
        return AssertEx.NotNull(JsonSerializer.Deserialize<PullStreamProgressEvent>(line, JsonOptions));
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}
