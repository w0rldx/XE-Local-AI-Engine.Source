namespace XE_Local_AI_Engine.Tests.Endpoints.Transcription;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The five managed source-build routes. What earns the attention here is that every refusal comes back as a
///     typed 409 with a reason code the SPA can branch on, rather than as an error the operator cannot act on.
/// </summary>
public sealed class TranscriptionSourceBuildEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    [RunOn(OS.Windows)]
    public async Task Start_OnNonLinux_Returns409NotLinux()
    {
        // The lane is Linux-only. The endpoint answers the OS question itself so a Windows node gets the same typed
        // conflict shape as every other refusal instead of an exception mapped to a 500. The branch needs a non-Linux
        // runner — the endpoint asks OperatingSystem.IsLinux() directly and there is no OS seam to inject — so on
        // this repo's Linux gate it reports a visible skip and Start_OnLinux_ReachesTheBuildService covers the half
        // that can run here.
        var build = new StubSourceBuildService();
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/source-build", OfficialCudaBody);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertEx.Equal("not-linux", body.GetProperty("reason").GetString());
        AssertEx.False(build.StartCalled, "A non-Linux node must not reach the build service.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Start_OnLinux_ReachesTheBuildService()
    {
        // The other side of the OS gate: a Linux node must pass the request through rather than refuse it, or the
        // whole lane would be unreachable on the only platform that supports it.
        var build = new StubSourceBuildService();
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/source-build", OfficialCudaBody);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(build.StartCalled, "A Linux node must reach the build service.");
    }

    [Test]
    public async Task Start_WhileBusy_Returns409RuntimeBusy()
    {
        // The 409 carries the activity snapshot, which is what lets the UI say "wait for the running transcription"
        // rather than only "failed".
        var build = new StubSourceBuildService
        {
            StartResult = new WhisperCppSourceBuildStartResult(WhisperCppSourceBuildStartOutcome.RuntimeBusy,
                Prerequisites: null,
                new WhisperRuntimeActivitySnapshot(ActiveTranscriptionCount: 1,
                    SpawnReadinessCount: 0,
                    ResidentProcessCount: 1,
                    MutationReserved: false,
                    EvictionReserved: false))
        };
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/source-build", OfficialCudaBody);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        if (!OperatingSystem.IsLinux())
        {
            AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
            return;
        }

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertEx.Equal("runtime-busy", body.GetProperty("reason").GetString());
        AssertEx.Equal(expected: 1, body.GetProperty("activity").GetProperty("activeTranscriptionCount").GetInt32());
        AssertEx.True(body.GetProperty("activity").GetProperty("isBusy").GetBoolean());
    }

    [Test]
    public async Task Start_MissingPrerequisites_Returns409PrerequisitesRatherThanRuntimeBusy()
    {
        // A missing compiler and a busy runtime are different problems with different fixes, so they must not share
        // a reason code.
        var build = new StubSourceBuildService
        {
            StartResult = new WhisperCppSourceBuildStartResult(WhisperCppSourceBuildStartOutcome.MissingPrerequisites,
                new WhisperCppSourceBuildPrerequisiteReport(false,
                    [new WhisperCppSourceBuildPrerequisiteItem("nvcc", false, "NVIDIA CUDA compiler (nvcc) is not available.")]))
        };
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/source-build", OfficialCudaBody);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        if (OperatingSystem.IsLinux())
        {
            var body = await ReadJsonAsync(response);
            AssertEx.Equal("prerequisites", body.GetProperty("reason").GetString());
        }
    }

    [Test]
    public async Task Start_InvalidCustomRepository_Returns400()
    {
        // Rejected by the validator, before the service is touched: a bad repository is a bad request, not a runtime
        // conflict.
        var build = new StubSourceBuildService();
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory,
            HttpMethod.Post,
            $"{ApiPrefix}/transcription/runtime/source-build",
            new
            {
                backend = "cuda",
                source = "custom",
                repository = "https://gitlab.com/someone/whisper.cpp",
                acknowledgeCustomSourceRisk = true
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.False(build.StartCalled, "An invalid repository must be rejected before the build service is reached.");
    }

    [Test]
    public async Task Start_CustomSourceWithoutAcknowledgement_Returns400()
    {
        var build = new StubSourceBuildService();
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory,
            HttpMethod.Post,
            $"{ApiPrefix}/transcription/runtime/source-build",
            new
            {
                backend = "cuda",
                source = "custom",
                repository = "https://github.com/someone/whisper.cpp",
                acknowledgeCustomSourceRisk = false
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.False(build.StartCalled, "Building unacknowledged third-party code must never reach the service.");
    }

    [Test]
    public async Task Status_ReturnsThePhaseAndBoundedLog()
    {
        var build = new StubSourceBuildService
        {
            Status = new WhisperCppSourceBuildStatus(WhisperCppSourceBuildPhase.SmokeTesting,
                IsRunning: true,
                Terminal: false,
                ["> cmake --build .", "[100%] Built target whisper-server"],
                LogStartSequence: 42,
                SanitizedError: null,
                Descriptor(),
                DateTimeOffset.UnixEpoch,
                CompletedAtUtc: null)
        };
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/runtime/source-build/status");

        // The phase is camel-cased on the wire; smokeTesting is the only two-word member and so the one that would
        // reveal a projection that stringified the enum instead.
        AssertEx.Equal("smokeTesting", body.GetProperty("phase").GetString());
        AssertEx.True(body.GetProperty("isRunning").GetBoolean());
        AssertEx.False(body.GetProperty("terminal").GetBoolean());
        AssertEx.Equal(expected: 42, body.GetProperty("logStartSequence").GetInt64());
        AssertEx.Equal(expected: 2, body.GetProperty("logLines").GetArrayLength());
        AssertEx.Equal("enginePinned", body.GetProperty("currentBuild").GetProperty("revisionMode").GetString());
        AssertEx.Equal("official", body.GetProperty("currentBuild").GetProperty("source").GetString());
        AssertEx.Equal("cuda", body.GetProperty("currentBuild").GetProperty("backend").GetString());
        AssertEx.Equal(WhisperCppReleasePins.PinnedSourceCommitSha, body.GetProperty("currentBuild").GetProperty("resolvedCommit").GetString());
    }

    [Test]
    public async Task Prerequisites_ReportEveryRowForTheRequestedBackend()
    {
        var build = new StubSourceBuildService();
        var probe = new StubPrerequisiteProbe
        {
            Report = new WhisperCppSourceBuildPrerequisiteReport(false,
            [
                new WhisperCppSourceBuildPrerequisiteItem("os-is-linux", true, "Linux host detected."),
                new WhisperCppSourceBuildPrerequisiteItem("nvcc", false, "NVIDIA CUDA compiler (nvcc) is not available.")
            ])
        };
        await using var factory = FactoryWith(build, probe);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/runtime/source-build/prerequisites?backend=cuda");

        AssertEx.Equal(WhisperBackend.Cuda, probe.RequestedBackend);
        AssertEx.Equal("cuda", body.GetProperty("backend").GetString());
        AssertEx.False(body.GetProperty("canBuild").GetBoolean());
        AssertEx.Equal(expected: 2, body.GetProperty("items").GetArrayLength());
        AssertEx.Equal("nvcc", body.GetProperty("items")[1].GetProperty("key").GetString());
        AssertEx.False(body.GetProperty("items")[1].GetProperty("satisfied").GetBoolean());
    }

    [Test]
    public async Task Cancel_WithNothingRunning_IsASuccessNotAnError()
    {
        // Cancelling an idle build is the caller asking for a state that already holds.
        var build = new StubSourceBuildService();
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/source-build/cancel", new { accepted = true });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(build.CancelCalled);
    }

    [Test]
    public async Task Remove_WhileBusy_Returns409RuntimeBusy()
    {
        var build = new StubSourceBuildService
        {
            RemoveResult = new WhisperCppSourceBuildRemoveResult(WhisperCppSourceBuildRemoveOutcome.RuntimeBusy,
                new WhisperRuntimeActivitySnapshot(ActiveTranscriptionCount: 0,
                    SpawnReadinessCount: 0,
                    ResidentProcessCount: 1,
                    MutationReserved: false,
                    EvictionReserved: false))
        };
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/source-build/remove", new { accepted = true });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertEx.Equal("runtime-busy", body.GetProperty("reason").GetString());
        AssertEx.Equal(expected: 1, body.GetProperty("activity").GetProperty("residentProcessCount").GetInt32());
    }

    [Test]
    public async Task Remove_WithNothingInstalled_IsASuccess()
    {
        // "No managed runtime" is what the caller asked for, and it is now true either way.
        var build = new StubSourceBuildService
        {
            RemoveResult = new WhisperCppSourceBuildRemoveResult(WhisperCppSourceBuildRemoveOutcome.NotInstalled)
        };
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/source-build/remove", new { accepted = true });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task Remove_WhenTheManagedTreeCannotBeDeleted_Returns409SourceBuildError()
    {
        // Remove is the in-app recovery from a fail-closed tombstone, so its own failure must arrive in the same
        // typed envelope Start uses. Left to the global handler it is a 500 the SPA cannot branch on.
        var build = new StubSourceBuildService
        {
            RemoveFailure = new WhisperRuntimeException("The managed whisper.cpp runtime could not be removed.")
        };
        await using var factory = FactoryWith(build);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/source-build/remove", new { accepted = true });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertEx.Equal("source-build-error", body.GetProperty("reason").GetString());
        AssertEx.Equal("The managed whisper.cpp runtime could not be removed.", body.GetProperty("message").GetString());
        AssertEx.False(body.GetProperty("activity").GetProperty("isBusy").GetBoolean(),
            "The envelope still carries the activity snapshot, and nothing holds the runtime here.");
    }

    [Test]
    public async Task Prerequisites_WithNoProbeSupplied_StillAnswersFromTheStub()
    {
        // The harness rule, asserted through the route that would break it. A test that leaves the real probe in the
        // container runs cmake, gcc, nvcc and nvidia-smi and creates the probe isolation tree under the operator's
        // LocalApplicationData; the real report carries a row per tool, the stub exactly one.
        await using var factory = FactoryWith(new StubSourceBuildService());
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/runtime/source-build/prerequisites?backend=cuda");

        AssertEx.Equal(expected: 1, body.GetProperty("items").GetArrayLength(),
            "A test that supplies no probe must still be answered by the stub, never by the real toolchain probe.");
        AssertEx.Equal("os-is-linux", body.GetProperty("items")[0].GetProperty("key").GetString());
        AssertEx.True(body.GetProperty("canBuild").GetBoolean());
    }

    [Test]
    [Arguments("POST", "transcription/runtime/source-build")]
    [Arguments("GET", "transcription/runtime/source-build/prerequisites?backend=cuda")]
    [Arguments("GET", "transcription/runtime/source-build/status")]
    [Arguments("POST", "transcription/runtime/source-build/cancel")]
    [Arguments("POST", "transcription/runtime/source-build/remove")]
    public async Task SourceBuildRoutes_WithAnAuthenticatedNonOperator_Return403(string method, string route)
    {
        // Proven with an authenticated non-operator and an operator control, never with an anonymous 401: swapping
        // the operator policy for plain authentication would keep a 401 assertion green.
        await using var factory = FactoryWith(new StubSourceBuildService());
        using var client = factory.CreateClient();

        using var forbidden = Request(method, route, factory.AddNonOperatorBearerToken);
        using var forbiddenResponse = await client.SendAsync(forbidden).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode,
            $"'{route}' must refuse an authenticated non-operator.");

        using var allowed = Request(method, route, factory.AddNodeBearerToken);
        using var allowedResponse = await client.SendAsync(allowed).ConfigureAwait(false);
        AssertEx.NotEqual(HttpStatusCode.Forbidden, allowedResponse.StatusCode, $"'{route}' must admit an operator.");
    }

    private static object OfficialCudaBody => new
    {
        backend = "cuda",
        source = "official",
        acknowledgeCustomSourceRisk = false
    };

    private static WhisperCppSourceBuildDescriptor Descriptor() =>
        new(WhisperBackend.Cuda,
            WhisperCppSourceSelection.Official,
            WhisperCppSourceBuildRequestValidation.OfficialRepository,
            WhisperCppSourceRevisionMode.EnginePinned,
            RequestedCommit: null,
            WhisperCppReleasePins.PinnedSourceCommitSha)
        {
            BuildId = Guid.NewGuid()
        };

    private static HttpRequestMessage Request(string method, string route, Action<HttpRequestMessage> authorize)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), $"{ApiPrefix}/{route}");
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new
            {
                backend = "cuda",
                source = "official",
                acknowledgeCustomSourceRisk = false,
                accepted = true
            });
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
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);
    }

    /// <summary>
    ///     Boots the host with both source-build seams substituted. The probe is replaced ALWAYS, not only when a
    ///     test supplies one: the real <c>WhisperCppSourceBuildPrerequisiteProbe</c> roots its isolation directory at
    ///     <c>LocalApplicationData</c>, so one endpoint test that left it in the container created
    ///     <c>~/.local/share/XE-Local-AI-Engine/whisper.cpp/source-build/.probe/</c> in the operator's real data
    ///     directory and ran cmake, gcc, nvcc and nvidia-smi on the gate machine.
    /// </summary>
    private static TestServerWebAppFactory FactoryWith(IWhisperCppSourceBuildService buildService,
        IWhisperCppSourceBuildPrerequisiteProbe? probe = null) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWhisperCppSourceBuildService>();
                services.AddSingleton(buildService);
                services.RemoveAll<IWhisperCppSourceBuildPrerequisiteProbe>();
                services.AddSingleton(probe ?? new StubPrerequisiteProbe());
            }
        };

    private sealed class StubSourceBuildService : IWhisperCppSourceBuildService
    {
        public bool StartCalled { get; private set; }

        public bool CancelCalled { get; private set; }

        public WhisperCppSourceBuildStartResult StartResult { get; init; } =
            new(WhisperCppSourceBuildStartOutcome.Started);

        public WhisperCppSourceBuildRemoveResult RemoveResult { get; init; } =
            new(WhisperCppSourceBuildRemoveOutcome.Removed);

        public WhisperCppSourceBuildStatus Status { get; init; } =
            new(WhisperCppSourceBuildPhase.Idle,
                IsRunning: false,
                Terminal: false,
                [],
                LogStartSequence: 0,
                SanitizedError: null,
                CurrentBuild: null,
                StartedAtUtc: null,
                CompletedAtUtc: null);

        public Task<WhisperCppSourceBuildStartResult> StartAsync(WhisperCppSourceBuildRequest request, CancellationToken ct)
        {
            StartCalled = true;
            return Task.FromResult(StartResult);
        }

        /// <summary>Set to make the service fail the way a stuck managed directory does.</summary>
        public WhisperRuntimeException? RemoveFailure { get; init; }

        public Task<WhisperCppSourceBuildRemoveResult> RemoveAsync(CancellationToken ct) =>
            RemoveFailure is null ? Task.FromResult(RemoveResult) : Task.FromException<WhisperCppSourceBuildRemoveResult>(RemoveFailure);

        public WhisperCppSourceBuildStatus GetStatus() => Status;

        public bool Cancel()
        {
            CancelCalled = true;
            return false;
        }

        public Task RecoverAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubPrerequisiteProbe : IWhisperCppSourceBuildPrerequisiteProbe
    {
        public WhisperBackend? RequestedBackend { get; private set; }

        public WhisperCppSourceBuildPrerequisiteReport Report { get; init; } =
            new(true, [new WhisperCppSourceBuildPrerequisiteItem("os-is-linux", true, "Linux host detected.")]);

        public Task<WhisperCppSourceBuildPrerequisiteReport> ProbeAsync(WhisperBackend backend, CancellationToken ct)
        {
            RequestedBackend = backend;
            return Task.FromResult(Report);
        }
    }
}
