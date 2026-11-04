namespace XE_Local_AI_Engine.Tests.Endpoints.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The experiment routes, the runtime routes' refusal during a GPU job, and the run hub's answer for an experiment id.</summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabExperimentEndpointTests
{
    private const string Experiments = "/api/local/v1/model-lab/experiments";

    [Test]
    [Arguments("GET", "")]
    [Arguments("POST", "")]
    [Arguments("GET", "/00000000-0000-0000-0000-000000000001")]
    [Arguments("DELETE", "/00000000-0000-0000-0000-000000000001")]
    [Arguments("GET", "/00000000-0000-0000-0000-000000000001/trace")]
    [Arguments("POST", "/00000000-0000-0000-0000-000000000001/cancel")]
    public async Task Anonymous_Is401(string method, string suffix)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), Experiments + suffix);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    [Arguments("GET", "")]
    [Arguments("POST", "")]
    [Arguments("GET", "/00000000-0000-0000-0000-000000000001")]
    [Arguments("DELETE", "/00000000-0000-0000-0000-000000000001")]
    [Arguments("GET", "/00000000-0000-0000-0000-000000000001/trace")]
    [Arguments("POST", "/00000000-0000-0000-0000-000000000001/cancel")]
    public async Task ANonOperator_Is403(string method, string suffix)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), Experiments + suffix);
        factory.AddNonOperatorBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task ANonOperator_CannotStartACheckpointDownload()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/model-lab/checkpoint-download")
        {
            Content = JsonContent.Create(new { repoId = "Qwen/Qwen3.5-0.8B", installedModelName = "org/Some-GGUF:Q4_K_M", licenseConfirmed = true })
        };
        factory.AddNonOperatorBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task ANonOperator_CannotReachTheHubAnExperimentIsSubscribedOn()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, LocalApiRoutes.Training.RunHub + "/negotiate");
        factory.AddNonOperatorBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task TheList_SaysWhetherThisPlatformCanTrace_AndWhyNot()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<ModelLabExperimentService>().PlatformProbe = static () => false;

        using var request = Authorized(factory, HttpMethod.Get, Experiments);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.False(body.RootElement.GetProperty("platformSupported").GetBoolean());
        AssertEx.True(body.RootElement.GetProperty("unsupportedMessage").GetString()!.Contains("Linux", StringComparison.Ordinal));
        AssertEx.Equal(ModelLabExperimentService.MaxRetainedExperiments, body.RootElement.GetProperty("maxRetainedExperiments").GetInt32());
    }

    [Test]
    public async Task ARefusedStart_Is409WithItsCode()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, Experiments, new
        {
            baseArtifactId = Guid.NewGuid(),
            installedModelName = "org/Some-GGUF:Q4_K_M",
            prompt = "hello",
            licenseConfirmed = true
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(nameof(ModelLabStartRefusal.CheckpointNotFound), body.RootElement.GetProperty("reason").GetString());
    }

    [Test]
    [Arguments("", "hello")]
    [Arguments("org/Some-GGUF:Q4_K_M", "   ")]
    public async Task AMalformedStart_Is400(string installedModelName, string prompt)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, Experiments, new
        {
            baseArtifactId = Guid.NewGuid(),
            installedModelName,
            prompt,
            licenseConfirmed = true
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task APromptOverTheLimit_Is400()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, Experiments, new
        {
            baseArtifactId = Guid.NewGuid(),
            installedModelName = "org/Some-GGUF:Q4_K_M",
            prompt = new string('x', ModelLabTraceSpec.MaxPromptCharacters + 1),
            licenseConfirmed = true
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    [Arguments("GET", "")]
    [Arguments("GET", "/trace")]
    [Arguments("POST", "/cancel")]
    [Arguments("DELETE", "")]
    public async Task AnUnknownExperiment_Is404(string method, string suffix)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, new HttpMethod(method), $"{Experiments}/{Guid.NewGuid()}{suffix}");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ASavedExperiment_ReadsBackWithItsResult_AndDeleting_RemovesIt()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>();
            await store.CreateRunningAsync(new ModelLabExperimentCreate
            {
                Id = id,
                RepoId = "Qwen/Qwen3.5-0.8B",
                Revision = "rev",
                BaseArtifactId = Guid.NewGuid(),
                InstalledModelName = "org/Some-GGUF:Q4_K_M",
                Prompt = "hello",
                LicenseConfirmationJson = new byte[] { 1 }
            });
            await store.CompleteAsync(id, new ModelLabExperimentCompletion
            {
                Status = ModelLabExperimentStatus.Succeeded,
                ResultJson = """{"promptTokens":3,"tokens":[{"id":9,"text":"hi"}],"text":"hi","seconds":0.5,"peakVramBytes":10,"blockCount":24}"""u8.ToArray()
            });
        }

        using (var request = Authorized(factory, HttpMethod.Get, $"{Experiments}/{id}"))
        using (var response = await client.SendAsync(request))
        {
            AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertEx.Equal("hello", body.RootElement.GetProperty("prompt").GetString());
            AssertEx.Equal("Succeeded", body.RootElement.GetProperty("status").GetString());
            var result = body.RootElement.GetProperty("result");
            AssertEx.Equal(expected: 3, result.GetProperty("promptTokens").GetInt32());
            AssertEx.Equal("hi", result.GetProperty("tokens")[0].GetProperty("text").GetString());
        }

        using (var request = Authorized(factory, HttpMethod.Get, Experiments))
        using (var response = await client.SendAsync(request))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertEx.Equal(JsonValueKind.Null, body.RootElement.GetProperty("experiments")[0].GetProperty("result").ValueKind, "A list leaves the tokens out.");
        }

        using (var request = Authorized(factory, HttpMethod.Delete, $"{Experiments}/{id}"))
        using (var response = await client.SendAsync(request))
        {
            AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    [Test]
    [Arguments("remove", GpuWorkKind.ModelLabExperiment)]
    [Arguments("install", GpuWorkKind.ModelLabExperiment)]
    [Arguments("remove", GpuWorkKind.TrainingRun)]
    [Arguments("remove", GpuWorkKind.Export)]
    public async Task TheTrainingRuntime_CannotBeChanged_WhileAJobUsingItRuns(string verb, GpuWorkKind kind)
    {
        var runtime = Substitute.For<ITrainingRuntimeService>();
        runtime.Cancel().Returns(false);
        await using var factory = RuntimeFactory(runtime);
        using var client = factory.CreateClient();
        var gate = factory.Services.GetRequiredService<IGpuWorkGate>();
        AssertEx.True(gate.TryBeginExclusive(kind, out var hold));
        using (hold)
        {
            await using var lease = await factory.Services.GetRequiredService<ILlamaServerProcessSupervisor>().AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None);
            AssertEx.NotNull(lease);

            using var request = Authorized(factory, HttpMethod.Post, $"/api/local/v1/training/runtime/{verb}");
            using var response = await client.SendAsync(request);

            AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertEx.Equal("gpu-job-running", body.RootElement.GetProperty("reason").GetString());
        }

        await runtime.DidNotReceiveWithAnyArgs().RemoveAsync(default);
        await runtime.DidNotReceiveWithAnyArgs().InstallAsync(default);
    }

    /// <summary>An evaluation runs on llama-server, so a lease does not make it a user of this runtime. A queue peek holds the gate with no lease and runs nothing.</summary>
    [Test]
    [Arguments(GpuWorkKind.EvaluationRun, true)]
    [Arguments(GpuWorkKind.TrainingRun, false)]
    public async Task TheTrainingRuntime_CanBeChanged_WhileTheGateIsHeldByNothingThatUsesIt(GpuWorkKind kind, bool holdLease)
    {
        var runtime = Substitute.For<ITrainingRuntimeService>();
        runtime.Cancel().Returns(false);
        await using var factory = RuntimeFactory(runtime);
        using var client = factory.CreateClient();
        AssertEx.True(factory.Services.GetRequiredService<IGpuWorkGate>().TryBeginExclusive(kind, out var hold));
        using (hold)
        {
            await using var lease = holdLease
                ? AssertEx.NotNull(await factory.Services.GetRequiredService<ILlamaServerProcessSupervisor>().AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None))
                : null;
            using var request = Authorized(factory, HttpMethod.Post, "/api/local/v1/training/runtime/remove");
            using var response = await client.SendAsync(request);
        }

        await runtime.Received(1).RemoveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheRunHub_AcceptsAnExperimentId_AndRefusesAnUnknownOne()
    {
        await using var factory = new TestServerWebAppFactory();
        var id = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().CreateRunningAsync(new ModelLabExperimentCreate
            {
                Id = id,
                RepoId = "Qwen/Qwen3.5-0.8B",
                Revision = "rev",
                BaseArtifactId = Guid.NewGuid(),
                InstalledModelName = "org/Some-GGUF:Q4_K_M",
                Prompt = "hello",
                LicenseConfirmationJson = new byte[] { 1 }
            });
        }

        var events = factory.Services.GetRequiredService<XE_Local_AI_Engine.Client.Services.Training.Runs.ITrainingRunEventBuffer>();
        _ = events.Append(id, XE_Local_AI_Engine.Client.Services.Training.Runs.TrainingRunEventKind.Phase, new XE_Local_AI_Engine.Client.Services.Training.Runs.TrainingRunPayload { Phase = "tracing" });
        var token = factory.CreateNodeAccessToken();
        await using var connection = new HubConnectionBuilder()
                                     .WithUrl(new Uri(factory.Server.BaseAddress, LocalApiRoutes.Training.RunHub),
                                         options =>
                                         {
                                             options.Transports = HttpTransportType.LongPolling;
                                             options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                             options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                                             options.Headers.Add("Origin", "http://localhost");
                                         })
                                     .WithNodeJsonProtocol()
                                     .Build();
        var replayed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = connection.On<JsonElement>("trainingRun.event", value => replayed.TrySetResult(value));
        await connection.StartAsync();

        await connection.InvokeAsync("Subscribe", id, 0L);
        var replayedEvent = await replayed.Task.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(id, replayedEvent.GetProperty("runId").GetGuid());
        _ = await AssertEx.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", Guid.NewGuid(), 0L), "An id that is neither a run nor an experiment is refused.");
    }

    private static TestServerWebAppFactory RuntimeFactory(ITrainingRuntimeService runtime) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ITrainingRuntimeService>();
                services.AddSingleton(runtime);
            }
        };

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string path, object? content = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (content is not null)
        {
            request.Content = JsonContent.Create(content);
        }

        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}
