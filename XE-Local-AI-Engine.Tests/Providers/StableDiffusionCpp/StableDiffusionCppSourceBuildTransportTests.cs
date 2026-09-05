namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

public sealed class StableDiffusionCppSourceBuildTransportTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void Mapper_PreservesBackendAndRevisionIntent()
    {
        var request = new StartStableDiffusionCppSourceBuildRequest
        {
            Backend = StableDiffusionCppSourceBackendDto.Vulkan,
            Source = StableDiffusionCppSourceSelectionDto.Custom,
            Repository = "https://github.com/example/fork",
            Commit = "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD",
            AcknowledgeCustomSourceRisk = true
        };

        var normalized = StableDiffusionCppSourceBuildRequestValidation.Normalize(request.ToContract());

        AssertEx.Equal(SdGpuBackend.Vulkan, normalized.Backend);
        AssertEx.Equal(StableDiffusionCppSourceSelection.Custom, normalized.Source);
        AssertEx.Equal("abcdefabcdefabcdefabcdefabcdefabcdefabcd", normalized.Commit);
    }

    [Test]
    public void Validator_RejectsCustomSourceWithoutRiskAcknowledgement()
    {
        var validator = new StartStableDiffusionCppSourceBuildRequestValidator();
        var result = validator.Validate(new StartStableDiffusionCppSourceBuildRequest
        {
            Backend = StableDiffusionCppSourceBackendDto.Cpu,
            Source = StableDiffusionCppSourceSelectionDto.Custom,
            Repository = "https://github.com/example/fork",
            AcknowledgeCustomSourceRisk = false
        });

        AssertEx.False(result.IsValid);
    }

    [Test]
    public void StatusMapper_UsesCamelCasePhaseAndSanitizedManagedRuntime()
    {
        var status = new StableDiffusionCppSourceBuildStatus(StableDiffusionCppSourceBuildPhase.SmokeTesting,
            IsRunning: true,
            Terminal: false,
            LogLines: [],
            LogStartSequence: 0,
            SanitizedError: null,
            CurrentBuild: null,
            StartedAtUtc: null,
            CompletedAtUtc: null);
        var installed = new StableDiffusionInstalledRuntimeState(StableDiffusionInstalledRuntimeValidity.Invalid,
            SdGpuBackend.Cuda,
            "https://github.com/example/fork",
            new string('a', 40),
            StableDiffusionCppSourceSelection.Custom,
            StableDiffusionCppSourceRevisionMode.ExplicitCommit,
            new string('a', 40),
            "/private/source/path",
            new string('b', 64),
            DateTimeOffset.UnixEpoch,
            "binary missing");

        var statusResponse = status.ToResponse();
        var installedResponse = installed.ToResponse();

        AssertEx.Equal("smokeTesting", statusResponse.Phase);
        AssertEx.Equal(StableDiffusionCppSourceBackendDto.Cuda, installedResponse.DesiredBackend);
        AssertEx.Equal("binary missing", installedResponse.InvalidReason);
        AssertEx.False(installedResponse.GetType().GetProperties()
                                        .Any(static property => property.Name is "SourceBuildPath" or "ServerSha256"));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task StartEndpoint_RuntimeBusy_ReturnsStableConflictShape()
    {
        var activity = new ImageRuntimeActivitySnapshot(2, 1, 1, false, false);
        var service = Substitute.For<IStableDiffusionCppSourceBuildService>();
        service.RecoverAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        service.StartAsync(Arg.Any<StableDiffusionCppSourceBuildRequest>(), Arg.Any<CancellationToken>())
               .Returns(new StableDiffusionCppSourceBuildStartResult(StableDiffusionCppSourceBuildStartOutcome.RuntimeBusy,
                   Activity: activity));
        var gate = Substitute.For<IImageRuntimeActivityGate>();
        gate.GetSnapshot().Returns(activity);

        await using var factory = CreateFactory(service, gate);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/images/runtime/source-build")
        {
            Content = JsonContent.Create(new StartStableDiffusionCppSourceBuildRequest
            {
                Backend = StableDiffusionCppSourceBackendDto.Cpu,
                Source = StableDiffusionCppSourceSelectionDto.Official,
                AcknowledgeCustomSourceRisk = false
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, responseText);
        using var body = JsonDocument.Parse(responseText);
        AssertEx.Equal("runtime-busy", body.RootElement.GetProperty("reason").GetString());
        AssertEx.Equal(2, body.RootElement.GetProperty("activity").GetProperty("activeJobCount").GetInt32());
        AssertEx.True(body.RootElement.GetProperty("activity").GetProperty("isBusy").GetBoolean());
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task StartEndpoint_SourceBuildExceptionDoesNotMisreportPrerequisites()
    {
        var activity = new ImageRuntimeActivitySnapshot(0, 0, 0, false, false);
        var service = Substitute.For<IStableDiffusionCppSourceBuildService>();
        service.RecoverAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        service.StartAsync(Arg.Any<StableDiffusionCppSourceBuildRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<StableDiffusionCppSourceBuildStartResult>>(_ =>
                   throw new StableDiffusionRuntimeException("Source-build recovery is still required."));
        var gate = Substitute.For<IImageRuntimeActivityGate>();
        gate.GetSnapshot().Returns(activity);

        await using var factory = CreateFactory(service, gate);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/images/runtime/source-build")
        {
            Content = JsonContent.Create(new StartStableDiffusionCppSourceBuildRequest
            {
                Backend = StableDiffusionCppSourceBackendDto.Cpu,
                Source = StableDiffusionCppSourceSelectionDto.Official,
                AcknowledgeCustomSourceRisk = false
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, responseText);
        using var body = JsonDocument.Parse(responseText);
        AssertEx.Equal("source-build-error", body.RootElement.GetProperty("reason").GetString());
    }

    [Test]
    public async Task Publisher_ProjectsStableCamelCaseWireShape()
    {
        object? payload = null;
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(proxy);
        var hub = Substitute.For<IHubContext<StableDiffusionCppSourceBuildHub>>();
        hub.Clients.Returns(clients);
        var publisher = new StableDiffusionCppSourceBuildEventPublisher(hub);
        proxy.SendCoreAsync(StableDiffusionCppSourceBuildEvents.StatusChanged, Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 payload = call.ArgAt<object?[]>(1)[0];
                 return Task.CompletedTask;
             });
        var descriptor = new StableDiffusionCppSourceBuildDescriptor(SdGpuBackend.Vulkan,
            StableDiffusionCppSourceSelection.Custom,
            "https://github.com/example/fork",
            StableDiffusionCppSourceRevisionMode.DefaultBranch,
            null,
            new string('a', 40))
        {
            BuildId = Guid.Parse("11111111-1111-4111-8111-111111111111")
        };

        await publisher.PublishStatusAsync(new StableDiffusionCppSourceBuildStatusEvent(StableDiffusionCppSourceBuildPhase.SmokeTesting, ["line"], 9, false, null, descriptor));

        var payloadJson = JsonSerializer.Serialize(payload, payload!.GetType(), WebJsonOptions);
        using var body = JsonDocument.Parse(payloadJson);
        AssertEx.Equal("smokeTesting", body.RootElement.GetProperty("phase").GetString());
        AssertEx.Equal("vulkan", body.RootElement.GetProperty("currentBuild").GetProperty("backend").GetString());
        AssertEx.Equal("custom", body.RootElement.GetProperty("currentBuild").GetProperty("source").GetString());
        AssertEx.Equal("defaultBranch", body.RootElement.GetProperty("currentBuild").GetProperty("revisionMode").GetString());
        AssertEx.Equal(expected: 9L, body.RootElement.GetProperty("appendedLogStartSequence").GetInt64());
    }

    [Test]
    public async Task EjectEndpoint_Busy_ReturnsActivityConflictWithoutRetrying()
    {
        var activity = new ImageRuntimeActivitySnapshot(1, 0, 1, false, false);
        var service = Substitute.For<IStableDiffusionCppSourceBuildService>();
        service.RecoverAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var gate = Substitute.For<IImageRuntimeActivityGate>();
        gate.GetSnapshot().Returns(activity);
        var supervisor = Substitute.For<IImageServerSupervisor>();
        supervisor.EvictAllAsync(Arg.Any<CancellationToken>())
                  .Returns(new ImageServerEvictAllResult(false, activity));
        var store = Substitute.For<IStableDiffusionInstalledRuntimeStore>();

        await using var factory = CreateFactory(service, gate, supervisor, store);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/images/runtime/eject")
        {
            Content = JsonContent.Create(new ImageRuntimeActionRequest
            {
                Accepted = true
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, responseText);
        using var body = JsonDocument.Parse(responseText);
        AssertEx.Equal("runtime-busy", body.RootElement.GetProperty("reason").GetString());
        AssertEx.Equal(1, body.RootElement.GetProperty("activity").GetProperty("activeJobCount").GetInt32());
        await supervisor.Received(1).EvictAllAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateImageJobEndpoint_RuntimeMutationActive_ReturnsOperatorSafeConflict()
    {
        var activity = new ImageRuntimeActivitySnapshot(0, 0, 0, true, false);
        var service = Substitute.For<IStableDiffusionCppSourceBuildService>();
        service.RecoverAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var gate = Substitute.For<IImageRuntimeActivityGate>();
        gate.GetSnapshot().Returns(activity);
        var coordinator = Substitute.For<IImageJobCoordinator>();
        coordinator.EnqueueAsync(Arg.Any<CreateImageJobInput>(), Arg.Any<CancellationToken>())
                   .Returns<Task<Guid>>(_ => throw new ImageRuntimeBusyException("The image runtime is changing; try again shortly."));

        await using var factory = CreateFactory(service, gate, coordinator: coordinator);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/images/jobs")
        {
            Content = JsonContent.Create(new CreateImageJobRequest
            {
                ModelName = "test-model",
                Prompt = "test prompt"
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal("runtime-busy", body.RootElement.GetProperty("reason").GetString());
        AssertEx.True(body.RootElement.GetProperty("activity").GetProperty("mutationReserved").GetBoolean());
        AssertEx.Equal("The image runtime is changing; try again shortly.", body.RootElement.GetProperty("message").GetString());
    }

    private static TestServerWebAppFactory CreateFactory(IStableDiffusionCppSourceBuildService service,
        IImageRuntimeActivityGate gate,
        IImageServerSupervisor? supervisor = null,
        IStableDiffusionInstalledRuntimeStore? store = null,
        IImageJobCoordinator? coordinator = null)
    {
        return new TestServerWebAppFactory
        {
            EnableDevelopmentMode = true,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IStableDiffusionCppSourceBuildService>();
                services.AddSingleton(service);
                services.RemoveAll<IImageRuntimeActivityGate>();
                services.AddSingleton(gate);
                if (supervisor is not null)
                {
                    services.RemoveAll<IImageServerSupervisor>();
                    services.AddSingleton(supervisor);
                }

                if (store is not null)
                {
                    services.RemoveAll<IStableDiffusionInstalledRuntimeStore>();
                    services.AddSingleton(store);
                }

                if (coordinator is not null)
                {
                    services.RemoveAll<IImageJobCoordinator>();
                    services.AddSingleton(coordinator);
                }
            }
        };
    }
}
