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

[Category(TestCategories.Integration)]
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

    [Test]
    public async Task RuntimeStatusEndpoint_WithNoManagedRuntime_ReportsNullRatherThanAnEmptyRecord()
    {
        // The SPA branches on managedRuntime being absent to offer the build, so "nothing installed" must arrive as an
        // explicit null and not as a zeroed record.
        var service = CreateBuildService();
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(0, 0, 0, false, false));

        await using var factory = CreateFactory(service, gate);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, "/api/local/v1/images/runtime");

        AssertEx.Equal(JsonValueKind.Null, body.GetProperty("managedRuntime").ValueKind);
        AssertEx.False(body.GetProperty("activity").GetProperty("isBusy").GetBoolean());
    }

    [Test]
    public async Task RuntimeStatusEndpoint_WithAManagedRuntime_ProjectsTheRecordAndTheLiveActivityCounts()
    {
        // Both halves of the envelope come from different collaborators — the installed-runtime store and the activity
        // gate — so a single read has to prove they are both projected, and the counts are what a mutation refusal
        // later explains itself with.
        var service = CreateBuildService();
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(2, 1, 3, true, false));
        var store = Substitute.For<IStableDiffusionInstalledRuntimeStore>();
        store.ReadAsync(Arg.Any<CancellationToken>())
             .Returns(new StableDiffusionInstalledRuntimeState(StableDiffusionInstalledRuntimeValidity.Active,
                 SdGpuBackend.Vulkan,
                 "https://github.com/leejet/stable-diffusion.cpp",
                 new string('c', 40),
                 StableDiffusionCppSourceSelection.Official,
                 StableDiffusionCppSourceRevisionMode.EnginePinned,
                 SourceRequestedCommit: null,
                 "/managed/source/path",
                 new string('d', 64),
                 DateTimeOffset.UnixEpoch.AddSeconds(7)));

        await using var factory = CreateFactory(service, gate, store: store);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, "/api/local/v1/images/runtime");

        var managed = body.GetProperty("managedRuntime");
        AssertEx.Equal("active", managed.GetProperty("validity").GetString());
        AssertEx.Equal("vulkan", managed.GetProperty("desiredBackend").GetString());
        AssertEx.Equal("enginePinned", managed.GetProperty("sourceRevisionMode").GetString());
        AssertEx.Equal(expected: 7_000L, managed.GetProperty("installedAtUtc").GetInt64());

        var activity = body.GetProperty("activity");
        AssertEx.Equal(expected: 2, activity.GetProperty("activeJobCount").GetInt32());
        AssertEx.Equal(expected: 1, activity.GetProperty("spawnReadinessCount").GetInt32());
        AssertEx.Equal(expected: 3, activity.GetProperty("residentProcessCount").GetInt32());
        AssertEx.True(activity.GetProperty("mutationReserved").GetBoolean());
        AssertEx.True(activity.GetProperty("isBusy").GetBoolean());
    }

    [Test]
    public async Task RemoveEndpoint_RuntimeBusy_ReportsTheRefusalsOwnSnapshotRatherThanTheGatesLater()
    {
        // The snapshot that belongs in the refusal is the one the remove attempt actually lost against. Re-reading the
        // gate afterwards would describe a different instant, so the two are stubbed to disagree and the result's own
        // snapshot has to win.
        var service = CreateBuildService();
        service.RemoveAsync(Arg.Any<CancellationToken>())
               .Returns(new StableDiffusionCppSourceBuildRemoveResult(StableDiffusionCppSourceBuildRemoveOutcome.RuntimeBusy,
                   new ImageRuntimeActivitySnapshot(3, 0, 0, false, false)));
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(0, 0, 0, false, false));

        await using var factory = CreateFactory(service, gate);
        using var client = factory.CreateClient();
        using var request = Authorized(factory, HttpMethod.Post, "/api/local/v1/images/runtime/source-build/remove", ActionBody);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, responseText);
        using var body = JsonDocument.Parse(responseText);
        AssertEx.Equal("runtime-busy", body.RootElement.GetProperty("reason").GetString());
        AssertEx.Equal(expected: 3, body.RootElement.GetProperty("activity").GetProperty("activeJobCount").GetInt32(),
            "The refusal must carry the snapshot the remove attempt lost against, not a later gate read.");
    }

    [Test]
    public async Task RemoveEndpoint_RuntimeBusyWithoutASnapshot_FallsBackToTheGate()
    {
        // StableDiffusionCppSourceBuildRemoveResult.Activity is optional, so a refusal can arrive without one. The
        // envelope still has to name what is holding the runtime, or the operator is told only "busy".
        var service = CreateBuildService();
        service.RemoveAsync(Arg.Any<CancellationToken>())
               .Returns(new StableDiffusionCppSourceBuildRemoveResult(StableDiffusionCppSourceBuildRemoveOutcome.RuntimeBusy));
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(0, 0, 4, false, false));

        await using var factory = CreateFactory(service, gate);
        using var client = factory.CreateClient();
        using var request = Authorized(factory, HttpMethod.Post, "/api/local/v1/images/runtime/source-build/remove", ActionBody);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, responseText);
        using var body = JsonDocument.Parse(responseText);
        AssertEx.Equal("runtime-busy", body.RootElement.GetProperty("reason").GetString());
        AssertEx.Equal(expected: 4, body.RootElement.GetProperty("activity").GetProperty("residentProcessCount").GetInt32());
        AssertEx.True(body.RootElement.GetProperty("activity").GetProperty("isBusy").GetBoolean());
    }

    [Test]
    [Arguments(StableDiffusionCppSourceBuildRemoveOutcome.Removed)]
    [Arguments(StableDiffusionCppSourceBuildRemoveOutcome.NotInstalled)]
    public async Task RemoveEndpoint_WhenNothingIsInstalledAfterwards_AnswersTheRuntimeStatus(StableDiffusionCppSourceBuildRemoveOutcome outcome)
    {
        // Removed and NotInstalled are the same answer to the caller — no managed runtime — and both return the runtime
        // status rather than a bespoke body, so a second GET is never needed to refresh the page.
        var service = CreateBuildService();
        service.RemoveAsync(Arg.Any<CancellationToken>())
               .Returns(new StableDiffusionCppSourceBuildRemoveResult(outcome));
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(0, 0, 0, false, false));

        await using var factory = CreateFactory(service, gate);
        using var client = factory.CreateClient();
        using var request = Authorized(factory, HttpMethod.Post, "/api/local/v1/images/runtime/source-build/remove", ActionBody);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, responseText);
        using var body = JsonDocument.Parse(responseText);
        AssertEx.Equal(JsonValueKind.Null, body.RootElement.GetProperty("managedRuntime").ValueKind);
        AssertEx.False(body.RootElement.GetProperty("activity").GetProperty("isBusy").GetBoolean());
        await service.Received(1).RemoveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelEndpoint_WithNothingRunning_IsIdempotentAndAnswersTheStatus()
    {
        // Cancel discards the service's bool: "there was nothing to cancel" is the state the caller asked for, so the
        // route stays a 200 and hands back the status instead of an error the SPA would have to special-case.
        var service = CreateBuildService();
        service.Cancel().Returns(false);
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(0, 0, 0, false, false));

        await using var factory = CreateFactory(service, gate);
        using var client = factory.CreateClient();
        using var request = Authorized(factory, HttpMethod.Post, "/api/local/v1/images/runtime/source-build/cancel", ActionBody);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, responseText);
        using var body = JsonDocument.Parse(responseText);
        AssertEx.Equal("idle", body.RootElement.GetProperty("phase").GetString());
        AssertEx.False(body.RootElement.GetProperty("isRunning").GetBoolean());
        _ = service.Received(1).Cancel();
    }

    [Test]
    public async Task PrerequisitesEndpoint_WithAnUndefinedBackend_Returns400WithoutProbing()
    {
        // The probe spawns compilers, so a backend that is not a real member has to be refused before it runs; left to
        // the mapper it would be an ArgumentOutOfRangeException mapped to a 500, after the toolchain had already run.
        // The refusal comes from FastEndpoints' model binder, which rejects any value that is not a defined member and
        // names the offending field — so GetStableDiffusionCppSourceBuildPrerequisitesRequestValidator's
        // Enum.IsDefined rule never runs on this route, and asserting its message here would only pin a body the
        // binder does not produce.
        var service = CreateBuildService();
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(0, 0, 0, false, false));
        var probe = Substitute.For<IStableDiffusionCppSourceBuildPrerequisiteProbe>();

        await using var factory = CreateFactory(service, gate, probe: probe);
        using var client = factory.CreateClient();
        using var request = Authorized(factory, HttpMethod.Get, "/api/local/v1/images/runtime/source-build/prerequisites?backend=99");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, responseText);
        AssertEx.Contains(responseText, "\"name\":\"backend\"",
            message: $"The refusal must name the field the operator has to correct. Body was: {responseText}");
        await probe.DidNotReceive().ProbeAsync(Arg.Any<SdGpuBackend>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PrerequisitesEndpoint_ProjectsTheChecklistForTheRequestedBackend()
    {
        // The backend travels as a query-string name and comes back on the response, so a binding that silently fell
        // back to the first enum member would report a CPU checklist under a CUDA heading.
        var service = CreateBuildService();
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(0, 0, 0, false, false));
        var probe = StubProbe(new StableDiffusionCppSourceBuildPrerequisiteReport(false,
        [
            new StableDiffusionCppSourceBuildPrerequisiteItem("os-is-linux", true, "Linux host detected."),
            new StableDiffusionCppSourceBuildPrerequisiteItem("nvcc", false, "NVIDIA CUDA compiler (nvcc) is not available.")
        ]));

        await using var factory = CreateFactory(service, gate, probe: probe);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, "/api/local/v1/images/runtime/source-build/prerequisites?backend=cuda");

        AssertEx.Equal("cuda", body.GetProperty("backend").GetString());
        AssertEx.False(body.GetProperty("canBuild").GetBoolean());
        AssertEx.Equal(expected: 2, body.GetProperty("items").GetArrayLength());
        AssertEx.Equal("nvcc", body.GetProperty("items")[1].GetProperty("key").GetString());
        AssertEx.False(body.GetProperty("items")[1].GetProperty("satisfied").GetBoolean());
        await probe.Received(1).ProbeAsync(SdGpuBackend.Cuda, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StatusEndpoint_ReturnsThePhaseLogWindowAndCurrentBuildVerbatim()
    {
        // The status route is the SPA's reconnect path: it has to answer the same projection the hub pushes, log
        // window included, or a client that missed events cannot resynchronize.
        var service = CreateBuildService(new StableDiffusionCppSourceBuildStatus(StableDiffusionCppSourceBuildPhase.SmokeTesting,
            IsRunning: true,
            Terminal: false,
            ["> cmake --build .", "[100%] Built target sd-server"],
            LogStartSequence: 42,
            SanitizedError: null,
            new StableDiffusionCppSourceBuildDescriptor(SdGpuBackend.Cuda,
                StableDiffusionCppSourceSelection.Official,
                "https://github.com/leejet/stable-diffusion.cpp",
                StableDiffusionCppSourceRevisionMode.EnginePinned,
                RequestedCommit: null,
                new string('e', 40))
            {
                BuildId = Guid.Parse("22222222-2222-4222-8222-222222222222")
            },
            DateTimeOffset.UnixEpoch,
            CompletedAtUtc: null));
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(0, 0, 0, false, false));

        await using var factory = CreateFactory(service, gate);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, "/api/local/v1/images/runtime/source-build/status");

        // smokeTesting is the only two-word phase, so it is the one that would expose a projection that stringified
        // the enum instead of going through ToWireString.
        AssertEx.Equal("smokeTesting", body.GetProperty("phase").GetString());
        AssertEx.True(body.GetProperty("isRunning").GetBoolean());
        AssertEx.Equal(expected: 42L, body.GetProperty("logStartSequence").GetInt64());
        AssertEx.Equal(expected: 2, body.GetProperty("logLines").GetArrayLength());
        AssertEx.Equal("22222222-2222-4222-8222-222222222222", body.GetProperty("currentBuild").GetProperty("buildId").GetString());
        AssertEx.Equal("cuda", body.GetProperty("currentBuild").GetProperty("backend").GetString());
        AssertEx.Equal(expected: 0L, body.GetProperty("startedAtUtc").GetInt64());
        AssertEx.Equal(JsonValueKind.Null, body.GetProperty("completedAtUtc").ValueKind);
    }

    [Test]
    [Arguments("GET", "images/runtime")]
    [Arguments("GET", "images/runtime/source-build/prerequisites?backend=cuda")]
    [Arguments("GET", "images/runtime/source-build/status")]
    [Arguments("POST", "images/runtime/source-build/cancel")]
    [Arguments("POST", "images/runtime/source-build/remove")]
    public async Task ImageRuntimeRoutes_AreOperatorGated(string method, string route)
    {
        // Proven with all three principals: an anonymous 401 alone would stay green if the operator policy were
        // downgraded to plain authentication, and the operator control keeps the 403 from passing vacuously.
        var service = CreateBuildService();
        service.RemoveAsync(Arg.Any<CancellationToken>())
               .Returns(new StableDiffusionCppSourceBuildRemoveResult(StableDiffusionCppSourceBuildRemoveOutcome.NotInstalled));
        var gate = CreateGate(new ImageRuntimeActivitySnapshot(0, 0, 0, false, false));

        await using var factory = CreateFactory(service, gate, probe: StubProbe());
        using var client = factory.CreateClient();

        using var anonymous = AuthRequest(method, route, static _ => { });
        using var anonymousResponse = await client.SendAsync(anonymous).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode, $"'{route}' must refuse an unauthenticated caller.");

        using var forbidden = AuthRequest(method, route, factory.AddNonOperatorBearerToken);
        using var forbiddenResponse = await client.SendAsync(forbidden).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode, $"'{route}' must refuse an authenticated non-operator.");

        using var allowed = AuthRequest(method, route, factory.AddNodeBearerToken);
        using var allowedResponse = await client.SendAsync(allowed).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, allowedResponse.StatusCode, $"'{route}' must admit the operator.");
    }

    private static object ActionBody =>
        new
        {
            accepted = true
        };

    private static HttpRequestMessage AuthRequest(string method, string route, Action<HttpRequestMessage> authorize)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), $"/api/local/v1/{route}");
        if (string.Equals(method, "POST", StringComparison.Ordinal))
        {
            request.Content = JsonContent.Create(ActionBody);
        }

        authorize(request);
        return request;
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string uri, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        factory.AddNodeBearerToken(request);
        return request;
    }

    private static async Task<JsonElement> GetJsonAsync(TestServerWebAppFactory factory, HttpClient client, string uri)
    {
        using var request = Authorized(factory, HttpMethod.Get, uri);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, responseText);
        return JsonSerializer.Deserialize<JsonElement>(responseText, WebJsonOptions);
    }

    private static IStableDiffusionCppSourceBuildService CreateBuildService(StableDiffusionCppSourceBuildStatus? status = null)
    {
        var service = Substitute.For<IStableDiffusionCppSourceBuildService>();
        service.RecoverAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        service.GetStatus()
               .Returns(status ?? new StableDiffusionCppSourceBuildStatus(StableDiffusionCppSourceBuildPhase.Idle,
                   IsRunning: false,
                   Terminal: false,
                   LogLines: [],
                   LogStartSequence: 0,
                   SanitizedError: null,
                   CurrentBuild: null,
                   StartedAtUtc: null,
                   CompletedAtUtc: null));
        return service;
    }

    private static IImageRuntimeActivityGate CreateGate(ImageRuntimeActivitySnapshot snapshot)
    {
        var gate = Substitute.For<IImageRuntimeActivityGate>();
        gate.GetSnapshot().Returns(snapshot);
        return gate;
    }

    /// <summary>
    ///     A prerequisite probe that answers from memory. The real
    ///     <c>StableDiffusionCppSourceBuildPrerequisiteProbe</c> spawns cmake, gcc, g++, ninja/make and git — plus nvcc
    ///     and nvidia-smi for CUDA — and roots its isolation tree at the operator's real
    ///     <c>LocalApplicationData</c>, so every host this class builds gets a substitute whether the test asserts on
    ///     the checklist or not.
    /// </summary>
    private static IStableDiffusionCppSourceBuildPrerequisiteProbe StubProbe(StableDiffusionCppSourceBuildPrerequisiteReport? report = null)
    {
        var probe = Substitute.For<IStableDiffusionCppSourceBuildPrerequisiteProbe>();
        probe.ProbeAsync(Arg.Any<SdGpuBackend>(), Arg.Any<CancellationToken>())
             .Returns(report ?? new StableDiffusionCppSourceBuildPrerequisiteReport(false,
                 [new StableDiffusionCppSourceBuildPrerequisiteItem("os-is-linux", true, "Linux host detected.")]));
        return probe;
    }

    /// <summary>
    ///     Boots the host with the source-build service and the activity gate substituted, and — unconditionally — the
    ///     prerequisite probe and the installed-runtime store too: the real probe runs the toolchain (see
    ///     <see cref="StubProbe" />) and the real store reads the operator's own managed-runtime record, which would
    ///     make "nothing installed" depend on the machine the gate runs on.
    /// </summary>
    private static TestServerWebAppFactory CreateFactory(IStableDiffusionCppSourceBuildService service,
        IImageRuntimeActivityGate gate,
        IImageServerSupervisor? supervisor = null,
        IStableDiffusionInstalledRuntimeStore? store = null,
        IImageJobCoordinator? coordinator = null,
        IStableDiffusionCppSourceBuildPrerequisiteProbe? probe = null)
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
                services.RemoveAll<IStableDiffusionCppSourceBuildPrerequisiteProbe>();
                services.AddSingleton(probe ?? StubProbe());
                services.RemoveAll<IStableDiffusionInstalledRuntimeStore>();
                services.AddSingleton(store ?? Substitute.For<IStableDiffusionInstalledRuntimeStore>());
                if (supervisor is not null)
                {
                    services.RemoveAll<IImageServerSupervisor>();
                    services.AddSingleton(supervisor);
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
