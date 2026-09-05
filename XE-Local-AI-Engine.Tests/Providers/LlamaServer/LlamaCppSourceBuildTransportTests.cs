namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Validators;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using OS = TUnit.Core.Enums.OS;

public sealed class LlamaCppSourceBuildTransportTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void Mapper_PreservesBackendAndRevisionIntent()
    {
        var request = new StartLlamaCppSourceBuildRequest
        {
            Backend = LlamaCppSourceBackendDto.Vulkan,
            Source = LlamaCppSourceSelectionDto.Custom,
            Repository = "https://github.com/example/fork",
            Commit = "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD",
            AcknowledgeCustomSourceRisk = true
        };

        var normalized = LlamaCppSourceBuildRequestValidation.Normalize(request.ToContract());

        AssertEx.Equal(LlamaCppSourceBackend.Vulkan, normalized.Backend);
        AssertEx.Equal(LlamaCppSourceSelection.Custom, normalized.Source);
        AssertEx.Equal("abcdefabcdefabcdefabcdefabcdefabcdefabcd", normalized.Commit);
    }

    [Test]
    public void Validator_RejectsCustomSourceWithoutRiskAcknowledgement()
    {
        var validator = new StartLlamaCppSourceBuildRequestValidator();
        var result = validator.Validate(new StartLlamaCppSourceBuildRequest
        {
            Backend = LlamaCppSourceBackendDto.Cpu,
            Source = LlamaCppSourceSelectionDto.Custom,
            Repository = "https://github.com/example/fork",
            AcknowledgeCustomSourceRisk = false
        });

        AssertEx.False(result.IsValid);
    }

    [Test]
    public void PrerequisiteValidator_RejectsUndefinedBackend()
    {
        var validator = new GetLlamaCppSourceBuildPrerequisitesRequestValidator();
        var result = validator.Validate(new GetLlamaCppSourceBuildPrerequisitesRequest
        {
            Backend = (LlamaCppSourceBackendDto)99
        });

        AssertEx.False(result.IsValid);
    }

    [Test]
    [Arguments(LlamaCppSourceBuildStartOutcome.AlreadyRunning, "already-building", 0)]
    [Arguments(LlamaCppSourceBuildStartOutcome.InsufficientDisk, "disk", 0)]
    [Arguments(LlamaCppSourceBuildStartOutcome.MissingPrerequisites, "prerequisites", 0)]
    [Arguments(LlamaCppSourceBuildStartOutcome.ProcessesRunning, "processes-running", 3)]
    [Arguments(LlamaCppSourceBuildStartOutcome.RuntimeBusy, "runtime-busy", 0)]
    public async Task StartEndpoint_MapsTypedAdmissionWithoutDuplicatingProbes(LlamaCppSourceBuildStartOutcome outcome,
        string expectedReason,
        int runningProcessCount)
    {
        if (OperatingSystem.IsWindows())
        {
            // The endpoint short-circuits with reason "not-linux" before it consults the service, so the substituted
            // admission outcome never reaches the response and every parameterised case reads back the same reason.
            Skip.Test("The source-build endpoint refuses with 'not-linux' before admission mapping runs.");
        }

        var service = Substitute.For<ILlamaCppSourceBuildService>();
        service.StartAsync(Arg.Any<LlamaCppSourceBuildRequest>(), Arg.Any<CancellationToken>())
               .Returns(new LlamaCppSourceBuildStartResult(outcome, RunningProcessCount: runningProcessCount));
        var prerequisiteProbe = Substitute.For<ILlamaCppSourceBuildPrerequisiteProbe>();
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        await using var factory = new TestServerWebAppFactory
        {
            EnableDevelopmentMode = true,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILlamaCppSourceBuildService>();
                services.AddSingleton(service);
                services.RemoveAll<ILlamaCppSourceBuildPrerequisiteProbe>();
                services.AddSingleton(prerequisiteProbe);
                services.RemoveAll<ILlamaServerProcessSupervisor>();
                services.AddSingleton(supervisor);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/model-fit/llamacpp/source-build")
        {
            Content = JsonContent.Create(new StartLlamaCppSourceBuildRequest
            {
                Backend = LlamaCppSourceBackendDto.Cpu,
                Source = LlamaCppSourceSelectionDto.Official,
                AcknowledgeCustomSourceRisk = false
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal(expectedReason, body.RootElement.GetProperty("reason").GetString());
        if (outcome == LlamaCppSourceBuildStartOutcome.ProcessesRunning)
        {
            AssertEx.Equal(runningProcessCount, body.RootElement.GetProperty("runningProcessCount").GetInt32());
        }

        await service.Received(1).StartAsync(Arg.Any<LlamaCppSourceBuildRequest>(), Arg.Any<CancellationToken>());
        await prerequisiteProbe.DidNotReceiveWithAnyArgs().ProbeAsync(default, default);
        _ = supervisor.DidNotReceiveWithAnyArgs().CountRunningProcesses();
        await supervisor.DidNotReceiveWithAnyArgs().TryAcquireRuntimeMutationLeaseAsync(default);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task StartEndpoint_WhenKeepModelWarmEnabled_BlocksBeforeStartingBuild()
    {
        var service = Substitute.For<ILlamaCppSourceBuildService>();
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithKeepModelWarm(enabled: true, modelName: "model-a")
                                                     .Build();
        await using var factory = new TestServerWebAppFactory
        {
            EnableDevelopmentMode = true,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILlamaCppSourceBuildService>();
                services.AddSingleton(service);
                services.RemoveAll<INodeRuntimeSettings>();
                services.AddSingleton(runtimeSettings);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/model-fit/llamacpp/source-build")
        {
            Content = JsonContent.Create(new StartLlamaCppSourceBuildRequest
            {
                Backend = LlamaCppSourceBackendDto.Cpu,
                Source = LlamaCppSourceSelectionDto.Official,
                AcknowledgeCustomSourceRisk = false
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal("keep-model-warm-enabled", body.RootElement.GetProperty("reason").GetString());
        AssertEx.Contains(body.RootElement.GetProperty("message").GetString()!, "Disable Keep Model Warm", StringComparison.Ordinal);
        await service.DidNotReceive().StartAsync(Arg.Any<LlamaCppSourceBuildRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task StartEndpoint_OfficialSource_HandsTheServiceAnUnnormalizedRequestAndStarts()
    {
        // Regression: the endpoint used to normalize the request before calling the service, which normalizes again.
        // The second pass saw the repository the FIRST pass had selected and rejected it as a client override, so every
        // official-source build answered 409 {"reason":"prerequisites","message":"The official source repository is
        // selected by the server."}. Assert the endpoint forwards the raw request and that real normalization succeeds.
        var service = Substitute.For<ILlamaCppSourceBuildService>();
        service.StartAsync(Arg.Any<LlamaCppSourceBuildRequest>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   // Mirrors what the production service does with whatever the endpoint handed it.
                   _ = LlamaCppSourceBuildRequestValidation.Normalize(call.Arg<LlamaCppSourceBuildRequest>());
                   return new LlamaCppSourceBuildStartResult(LlamaCppSourceBuildStartOutcome.Started);
               });
        service.GetStatus().Returns(new LlamaCppSourceBuildStatus(LlamaCppSourceBuildPhase.Cloning,
            IsRunning: true,
            Terminal: false,
            LogLines: [],
            LogStartSequence: 0,
            SanitizedError: null,
            CurrentBuild: null,
            StartedAtUtc: null,
            CompletedAtUtc: null));
        await using var factory = new TestServerWebAppFactory
        {
            EnableDevelopmentMode = true,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILlamaCppSourceBuildService>();
                services.AddSingleton(service);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/model-fit/llamacpp/source-build")
        {
            Content = JsonContent.Create(new StartLlamaCppSourceBuildRequest
            {
                Backend = LlamaCppSourceBackendDto.Cpu,
                Source = LlamaCppSourceSelectionDto.Official,
                AcknowledgeCustomSourceRisk = false
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.True(body.RootElement.GetProperty("started").GetBoolean());
        await service.Received(1).StartAsync(Arg.Is<LlamaCppSourceBuildRequest>(sourceRequest =>
                sourceRequest.Source == LlamaCppSourceSelection.Official && sourceRequest.Repository == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(LlamaCppSourceBuildStartOutcome.AlreadyRunning, "already-building", 0)]
    [Arguments(LlamaCppSourceBuildStartOutcome.InsufficientDisk, "disk", 0)]
    [Arguments(LlamaCppSourceBuildStartOutcome.MissingPrerequisites, "prerequisites", 0)]
    [Arguments(LlamaCppSourceBuildStartOutcome.ProcessesRunning, "processes-running", 4)]
    [Arguments(LlamaCppSourceBuildStartOutcome.RuntimeBusy, "runtime-busy", 0)]
    [RunOn(OS.Linux)]
    public async Task LegacyStartEndpoint_MapsTypedAdmissionWithoutDuplicatingProbes(LlamaCppSourceBuildStartOutcome outcome,
        string expectedReason,
        int runningProcessCount)
    {
        var service = Substitute.For<ILlamaCppSourceBuildService>();
        service.StartAsync(Arg.Any<LlamaCppSourceBuildRequest>(), Arg.Any<CancellationToken>())
               .Returns(new LlamaCppSourceBuildStartResult(outcome, RunningProcessCount: runningProcessCount));
        var prerequisiteProbe = Substitute.For<ICudaBuildPrerequisiteProbe>();
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        await using var factory = new TestServerWebAppFactory
        {
            EnableDevelopmentMode = true,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILlamaCppSourceBuildService>();
                services.AddSingleton(service);
                services.RemoveAll<ICudaBuildPrerequisiteProbe>();
                services.AddSingleton(prerequisiteProbe);
                services.RemoveAll<ILlamaServerProcessSupervisor>();
                services.AddSingleton(supervisor);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/model-fit/llamacpp/cuda-build");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal(expectedReason, body.RootElement.GetProperty("reason").GetString());
        if (outcome == LlamaCppSourceBuildStartOutcome.ProcessesRunning)
        {
            AssertEx.Equal(runningProcessCount, body.RootElement.GetProperty("runningProcessCount").GetInt32());
        }

        await service.Received(1).StartAsync(Arg.Is<LlamaCppSourceBuildRequest>(sourceRequest =>
                sourceRequest.Backend == LlamaCppSourceBackend.Cuda
                && sourceRequest.Source == LlamaCppSourceSelection.Official),
            Arg.Any<CancellationToken>());
        await prerequisiteProbe.DidNotReceiveWithAnyArgs().ProbeAsync(default);
        _ = supervisor.DidNotReceiveWithAnyArgs().CountRunningProcesses();
        await supervisor.DidNotReceiveWithAnyArgs().TryAcquireRuntimeMutationLeaseAsync(default);
    }

    [Test]
    [Arguments(LlamaCppSourceBuildStartOutcome.Started, CudaBuildStartOutcome.Started)]
    [Arguments(LlamaCppSourceBuildStartOutcome.AlreadyRunning, CudaBuildStartOutcome.AlreadyRunning)]
    public async Task LegacyAdapter_PreservesCompatibleStartOutcomes(LlamaCppSourceBuildStartOutcome sourceOutcome,
        CudaBuildStartOutcome expected)
    {
        var service = Substitute.For<ILlamaCppSourceBuildService>();
        service.StartAsync(Arg.Any<LlamaCppSourceBuildRequest>(), Arg.Any<CancellationToken>())
               .Returns(new LlamaCppSourceBuildStartResult(sourceOutcome));
        var adapter = new LegacyCudaBuildServiceAdapter(service);

        AssertEx.Equal(expected, await adapter.StartAsync(CancellationToken.None));
    }

    [Test]
    [Arguments(LlamaCppSourceBuildStartOutcome.InsufficientDisk, "free disk")]
    [Arguments(LlamaCppSourceBuildStartOutcome.MissingPrerequisites, "prerequisites")]
    [Arguments(LlamaCppSourceBuildStartOutcome.ProcessesRunning, "running llama.cpp models")]
    [Arguments(LlamaCppSourceBuildStartOutcome.RuntimeBusy, "runtime change")]
    public async Task LegacyAdapter_DoesNotCollapseAdmissionFailuresToAlreadyRunning(LlamaCppSourceBuildStartOutcome sourceOutcome,
        string expectedMessage)
    {
        var service = Substitute.For<ILlamaCppSourceBuildService>();
        service.StartAsync(Arg.Any<LlamaCppSourceBuildRequest>(), Arg.Any<CancellationToken>())
               .Returns(new LlamaCppSourceBuildStartResult(sourceOutcome));
        var adapter = new LegacyCudaBuildServiceAdapter(service);

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => adapter.StartAsync(CancellationToken.None));

        AssertEx.Contains(exception.Message, expectedMessage);
    }

    [Test]
    public void RuntimeMapper_PreservesExplicitInstalledSourceSelection()
    {
        var installed = new InstalledRuntimeState("b1", "source", "sha", GpuVariant.Cpu, DateTimeOffset.UtcNow,
            "/managed/source", LlamaCppSourceBuildRequestValidation.OfficialRepository, new string('a', 40),
            LlamaCppSourceRevisionMode.DefaultBranch, null, LlamaCppSourceSelection.Custom);

        var response = LlamaCppUpdateSnapshot.Empty.ToRuntimeStatusResponse(installed, "b1", runningProcessCount: 0);

        AssertEx.Equal(LlamaCppSourceSelectionDto.Custom, response.Installed!.SourceSelection);
    }

    [Test]
    public void StatusMapper_PreservesLogStartSequence()
    {
        var status = new LlamaCppSourceBuildStatus(LlamaCppSourceBuildPhase.Building,
            true,
            false,
            ["line"],
            37,
            null,
            null,
            null,
            null);

        var response = status.ToResponse();

        AssertEx.Equal(expected: 37L, response.LogStartSequence);
        AssertEx.Equal("line", response.LogLines.Single());
    }

    [Test]
    public async Task Remove_AcquiresLeaseChecksProcessesRemovesAndDisposesInOrder()
    {
        var order = new List<string>();
#pragma warning disable CA2000 // Ownership transfers through the supervisor to the guard's TryRemoveAsync, which disposes the lease.
        var lease = new RecordingMutationLease(order);
#pragma warning restore CA2000
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("acquire");
            return Task.FromResult<ILlamaServerRuntimeMutationLease?>(lease);
        });
        supervisor.CountRunningProcesses().Returns(_ =>
        {
            order.Add("count");
            return 0;
        });
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var activity = Substitute.For<ILlamaCppSourceBuildActivity>();
        binaryManager.RemoveSourceBuildAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("remove");
            return Task.CompletedTask;
        });

        var (removed, _, _) =
            await LlamaCppPrebuiltRuntimeMutationGuard.TryRemoveAsync(supervisor,
                activity,
                binaryManager.RemoveSourceBuildAsync,
                CancellationToken.None);

        AssertEx.True(removed);
        AssertEx.Equal("acquire|count|remove|dispose", string.Join('|', order));
    }

    [Test]
    public async Task Remove_WhenLeaseCannotBeAcquired_ReturnsConflictSeamWithoutDeleting()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(null));
        supervisor.CountRunningProcesses().Returns(1);
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var activity = Substitute.For<ILlamaCppSourceBuildActivity>();

        var (removed, runningProcessCount, _) =
            await LlamaCppPrebuiltRuntimeMutationGuard.TryRemoveAsync(supervisor,
                activity,
                binaryManager.RemoveSourceBuildAsync,
                CancellationToken.None);

        AssertEx.False(removed);
        AssertEx.Equal(expected: 1, runningProcessCount);
        await binaryManager.DidNotReceiveWithAnyArgs().RemoveSourceBuildAsync(default);
    }

    [Test]
    public async Task Remove_WhenBuildIsActive_FailsBeforeLeaseWithoutDeleting()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var activity = Substitute.For<ILlamaCppSourceBuildActivity>();
        activity.ActiveBuildId.Returns(Guid.NewGuid());

        var (removed, _, buildActive) =
            await LlamaCppPrebuiltRuntimeMutationGuard.TryRemoveAsync(supervisor,
                activity,
                binaryManager.RemoveSourceBuildAsync,
                CancellationToken.None);

        AssertEx.False(removed);
        AssertEx.True(buildActive);
        await supervisor.DidNotReceiveWithAnyArgs().TryAcquireRuntimeMutationLeaseAsync(default);
        await binaryManager.DidNotReceiveWithAnyArgs().RemoveSourceBuildAsync(default);
    }

    [Test]
    public async Task Remove_WhenBuildStartsWhileLeaseIsAcquired_RechecksAndDoesNotDelete()
    {
#pragma warning disable CA2000 // Ownership transfers through the supervisor to the guard's TryRemoveAsync, which disposes the lease.
        var lease = new RecordingMutationLease([]);
#pragma warning restore CA2000
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<ILlamaServerRuntimeMutationLease?>(lease));
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var activity = Substitute.For<ILlamaCppSourceBuildActivity>();
        activity.ActiveBuildId.Returns((Guid?)null, Guid.NewGuid());

        var (removed, _, buildActive) =
            await LlamaCppPrebuiltRuntimeMutationGuard.TryRemoveAsync(supervisor,
                activity,
                binaryManager.RemoveSourceBuildAsync,
                CancellationToken.None);

        AssertEx.False(removed);
        AssertEx.True(buildActive);
        await binaryManager.DidNotReceiveWithAnyArgs().RemoveSourceBuildAsync(default);
    }

    [Test]
    public async Task Remove_WhenRemovingTheCudaBuild_InvokesTheCudaRemovalUnderTheSameGate()
    {
        var order = new List<string>();
#pragma warning disable CA2000 // Ownership transfers through the supervisor to the guard's TryRemoveAsync, which disposes the lease.
        var lease = new RecordingMutationLease(order);
#pragma warning restore CA2000
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.TryAcquireRuntimeMutationLeaseAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("acquire");
            return Task.FromResult<ILlamaServerRuntimeMutationLease?>(lease);
        });
        supervisor.CountRunningProcesses().Returns(_ =>
        {
            order.Add("count");
            return 0;
        });
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        var activity = Substitute.For<ILlamaCppSourceBuildActivity>();
        binaryManager.RemoveCudaSourceBuildAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("remove");
            return Task.CompletedTask;
        });

        var (removed, _, _) =
            await LlamaCppPrebuiltRuntimeMutationGuard.TryRemoveAsync(supervisor,
                activity,
                binaryManager.RemoveCudaSourceBuildAsync,
                CancellationToken.None);

        AssertEx.True(removed);
        AssertEx.Equal("acquire|count|remove|dispose", string.Join('|', order));
    }

    [Test]
    public async Task Publisher_ForwardsOnlyLegacyPinnedCudaToLegacyHub()
    {
        object? genericPayload = null;
        var genericProxy = Substitute.For<IClientProxy>();
        var legacyProxy = Substitute.For<IClientProxy>();
        var genericClients = Substitute.For<IHubClients>();
        var legacyClients = Substitute.For<IHubClients>();
        genericClients.All.Returns(genericProxy);
        legacyClients.All.Returns(legacyProxy);
        var genericHub = Substitute.For<IHubContext<LlamaCppSourceBuildHub>>();
        var legacyHub = Substitute.For<IHubContext<CudaBuildHub>>();
        genericHub.Clients.Returns(genericClients);
        legacyHub.Clients.Returns(legacyClients);
        var publisher = new LlamaCppSourceBuildEventPublisher(genericHub, legacyHub);
        genericProxy.SendCoreAsync(LlamaCppSourceBuildHubEvents.StatusChanged, Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        genericPayload = callInfo.ArgAt<object?[]>(1)[0];
                        return Task.CompletedTask;
                    });

        var buildId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var custom = new LlamaCppSourceBuildDescriptor(GpuVariant.Cpu,
            LlamaCppSourceSelection.Custom,
            "https://github.com/example/fork",
            LlamaCppSourceRevisionMode.DefaultBranch,
            null,
            new string('a', 40))
        {
            BuildId = buildId
        };
        await publisher.PublishStatusAsync(new LlamaCppSourceBuildStatusHubEvent("Building", [], 41, false, null, custom));

        await genericProxy.Received(1).SendCoreAsync(LlamaCppSourceBuildHubEvents.StatusChanged, Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        await legacyProxy.DidNotReceive().SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
        var payloadJson = JsonSerializer.Serialize(genericPayload, genericPayload!.GetType(), WebJsonOptions);
        using var payload = JsonDocument.Parse(payloadJson);
        var currentBuild = payload.RootElement.GetProperty("currentBuild");
        AssertEx.Equal(buildId, currentBuild.GetProperty("buildId").GetGuid());
        AssertEx.Equal("cpu", currentBuild.GetProperty("backend").GetString());
        AssertEx.Equal("custom", currentBuild.GetProperty("source").GetString());
        AssertEx.Equal("defaultBranch", currentBuild.GetProperty("revisionMode").GetString());
        AssertEx.Equal(expected: 41L, payload.RootElement.GetProperty("appendedLogStartSequence").GetInt64());

        var legacy = new LlamaCppSourceBuildDescriptor(GpuVariant.Cuda,
            LlamaCppSourceSelection.Official,
            LlamaCppSourceBuildRequestValidation.OfficialRepository,
            LlamaCppSourceRevisionMode.EnginePinned,
            null,
            LlamaCppReleasePins.PinnedSourceCommitSha);
        await publisher.PublishStatusAsync(new LlamaCppSourceBuildStatusHubEvent("Building", ["line"], 42, false, null, legacy));

        await legacyProxy.Received(1).SendCoreAsync(CudaBuildHubEvents.StatusChanged, Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    private sealed class RecordingMutationLease(List<string> order) : ILlamaServerRuntimeMutationLease
    {
        public ValueTask DisposeAsync()
        {
            order.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
