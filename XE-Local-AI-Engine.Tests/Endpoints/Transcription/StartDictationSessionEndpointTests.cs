namespace XE_Local_AI_Engine.Tests.Endpoints.Transcription;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Transcription;

/// <summary>
///     The dictation-session route over the wired host. Only the whisper supervisor and transcriber are fakes; the
///     service, the registry and the store are real, because "a live session exists and no row does" is the contract.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class StartDictationSessionEndpointTests
{
    private const string Route = "/api/local/v1/transcription/dictation-sessions";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task StartDictationSession_WithoutAToken_IsUnauthorized()
    {
        await using var factory = FactoryWith(new FakeWhisperServerSupervisor());
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new
            {
            })
        };
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task StartDictationSession_AuthenticatedNonOperator_Returns403()
    {
        var supervisor = new FakeWhisperServerSupervisor();
        await using var factory = FactoryWith(supervisor);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNonOperatorBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertEx.Empty(supervisor.EnsureRunningCalls, "a refused caller must not warm the runtime.");
    }

    [Test]
    public async Task StartDictationSession_Operator_Succeeds()
    {
        // The positive control for the 403 above: without it a route broken for everyone would pass that test.
        await using var factory = FactoryWith(new FakeWhisperServerSupervisor());
        using var client = factory.CreateClient();

        using var request = Authorized(factory);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task StartDictationSession_Returns404_WhenTranscriptionDisabled()
    {
        // The 404 is the SPA's signal to hide the microphone button, so it is asserted rather than assumed.
        await using var factory = FactoryWith(new FakeWhisperServerSupervisor(), new Dictionary<string, string?>
        {
            ["Transcription:Enabled"] = "false"
        });
        using var client = factory.CreateClient();

        using var request = Authorized(factory);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task StartDictationSession_WhenTheRuntimeFailsToWarm_ReturnsBadRequestAndRegistersNothing()
    {
        var supervisor = new FakeWhisperServerSupervisor
        {
            Failure = new WhisperRuntimeException("The transcription runtime did not become ready.")
        };
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        await using var factory = FactoryWith(supervisor, registry: registry);
        using var client = factory.CreateClient();

        using var request = Authorized(factory);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "The transcription runtime did not become ready.");
        await registry.DidNotReceive().StartLiveSessionAsync(Arg.Any<Guid>(), Arg.Any<LiveSessionOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartDictationSession_StartsAPersistFreeSessionAndWritesNoRows()
    {
        var supervisor = new FakeWhisperServerSupervisor();
        await using var factory = FactoryWith(supervisor);
        using var client = factory.CreateClient();

        using var request = Authorized(factory);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);
        var sessionId = body.GetProperty("sessionId").GetGuid();
        AssertEx.NotEqual(Guid.Empty, sessionId);
        AssertEx.Equal(expected: 0L, body.GetProperty("lastSeq").GetInt64());
        AssertEx.True(body.GetProperty("startedAtUtc").GetInt64() > 0, "the start time is unix milliseconds.");

        AssertEx.True(factory.Services.GetRequiredService<ILiveTranscriptionSessionRegistry>().IsLive(sessionId),
            "the returned id must name a live session the hub accepts audio for.");
        AssertEx.Equal(expected: 1, supervisor.EnsureRunningCalls.Count, "the runtime is warmed before the session is handed out.");

        using var scope = factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ITranscriptionService>();
        AssertEx.Null(await sessions.GetSessionSummaryAsync(sessionId, CancellationToken.None), "a dictation session never has a row.");
    }

    [Test]
    [Arguments("en")]
    [Arguments("de")]
    public async Task StartDictationSession_WithALanguage_ForwardsItToTheRegistry(string language)
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        await using var factory = FactoryWith(new FakeWhisperServerSupervisor(), registry: registry);
        using var client = factory.CreateClient();

        using var request = Authorized(factory);
        request.Content = JsonContent.Create(new
        {
            language
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        await registry.Received(1).StartLiveSessionAsync(Arg.Any<Guid>(),
            Arg.Is<LiveSessionOptions>(options => options.Language == language && !options.Persist),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"language":null}""")]
    public async Task StartDictationSession_WithoutALanguage_DetectsIt(string body)
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        await using var factory = FactoryWith(new FakeWhisperServerSupervisor(), registry: registry);
        using var client = factory.CreateClient();

        using var request = Authorized(factory);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        await registry.Received(1).StartLiveSessionAsync(Arg.Any<Guid>(),
            Arg.Is<LiveSessionOptions>(options => options.Language == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("e")]
    [Arguments("")]
    [Arguments("not-a-code")]
    public async Task StartDictationSession_WithAMalformedLanguage_Returns400AndRegistersNothing(string language)
    {
        var supervisor = new FakeWhisperServerSupervisor();
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        await using var factory = FactoryWith(supervisor, registry: registry);
        using var client = factory.CreateClient();

        using var request = Authorized(factory);
        request.Content = JsonContent.Create(new
        {
            language
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "2-8 character code");
        AssertEx.Empty(supervisor.EnsureRunningCalls, "a refused request must not warm the runtime.");
        await registry.DidNotReceive().StartLiveSessionAsync(Arg.Any<Guid>(), Arg.Any<LiveSessionOptions>(), Arg.Any<CancellationToken>());
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new
            {
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static TestServerWebAppFactory FactoryWith(IWhisperServerSupervisor supervisor,
        Dictionary<string, string?>? configuration = null,
        ILiveTranscriptionSessionRegistry? registry = null) =>
        new()
        {
            AdditionalConfiguration = configuration,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWhisperTranscriber>();
                services.AddSingleton<IWhisperTranscriber>(new ScriptedWhisperTranscriber(_ => []));
                services.RemoveAll<IWhisperServerSupervisor>();
                services.AddSingleton(supervisor);
                if (registry is not null)
                {
                    services.RemoveAll<ILiveTranscriptionSessionRegistry>();
                    services.AddSingleton(registry);
                }
            }
        };
}
