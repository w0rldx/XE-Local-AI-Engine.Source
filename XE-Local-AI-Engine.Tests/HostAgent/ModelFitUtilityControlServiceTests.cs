namespace XE_Local_AI_Engine.Tests.HostAgent;

using global::Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;
using XE_Local_AI_Engine.HostAgent.Linux.Services;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Handler coverage for <see cref="ModelFitUtilityControlService" /> (plan Marker 2) driven entirely by
///     <see cref="FakeDockerRuntimeClient" /> — NO Docker. Asserts the image allowlist gate (only an allowlisted pinned
///     ref runs; a non-allowlisted repo / :latest / unpinned ref is rejected WITHOUT running), the server-built argv
///     (HW overrides BEFORE the recommend subcommand; bench requires a model name), cancellation/timeout cleanup, the
///     debug-only retain-on-failure behavior, and that the request has no arbitrary-command field.
/// </summary>
public sealed class ModelFitUtilityControlServiceTests
{
    private const string AllowedReference =
        "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";

    private static (ModelFitUtilityControlService Service, FakeDockerRuntimeClient Docker) CreateService(ModelFitUtilityOptions? options = null)
    {
        var docker = new FakeDockerRuntimeClient(TimeProvider.System);
        var service = new ModelFitUtilityControlService(
            docker,
            Options.Create(options ?? new ModelFitUtilityOptions()),
            TimeProvider.System,
            NullLogger<ModelFitUtilityControlService>.Instance);
        return (service, docker);
    }

    private static RunModelFitUtilityRequest RecommendRequest()
    {
        return new RunModelFitUtilityRequest
        {
            ImageReference = AllowedReference,
            Operation = ModelFitOperationMessage.ModelFitOperationRecommend,
            UseCase = "coding",
            Limit = 5,
            ProviderName = "ollama",
            Network = ModelFitNetworkModeMessage.ModelFitNetworkModeNone
        };
    }

    [Test]
    public async Task RunModelFitUtility_WhenAllowlistedRecommend_RunsAndSucceeds()
    {
        var (service, docker) = CreateService();
        docker.ScriptUtilityRun(0, """{"models":[]}""");

        var reply = await service.RunModelFitUtility(RecommendRequest(), Context());

        AssertEx.Equal(ModelFitTerminalStatusMessage.Succeeded, reply.Status);
        AssertEx.Equal(0, reply.ExitCode);
        AssertEx.True(reply.Completed);
        AssertEx.Equal("""{"models":[]}""", reply.StandardOutput);
        AssertEx.Equal(1, docker.UtilityRunCount);
    }

    [Test]
    public async Task RunModelFitUtility_RecommendArgv_PutsHardwareOverridesBeforeSubcommand()
    {
        var (service, docker) = CreateService();
        var request = RecommendRequest();
        request.CpuCoresOverride = 4;
        request.RamOverrideGb = 16;
        request.VramOverrideGb = 8;
        request.Limit = 3;

        await service.RunModelFitUtility(request, Context());

        var argv = docker.LastUtilityRunSpec!.Arguments;
        // Overrides MUST precede the subcommand (verified Marker 0: placing them after exits 2).
        AssertEx.Equal("--cpu-cores", argv[0]);
        AssertEx.Equal("4", argv[1]);
        AssertEx.Equal("--ram", argv[2]);
        AssertEx.Equal("16G", argv[3]);
        AssertEx.Equal("--memory", argv[4]);
        AssertEx.Equal("8G", argv[5]);
        AssertEx.Equal("recommend", argv[6]);
        AssertEx.Equal("--json", argv[7]);
        AssertEx.Equal("--use-case", argv[8]);
        AssertEx.Equal("coding", argv[9]);
        AssertEx.Equal("--limit", argv[10]);
        AssertEx.Equal("3", argv[11]);
        // recommend never carries a provider url, and runs with no network (offline).
        AssertEx.False(argv.Contains("--url"), "recommend must not pass a provider url.");
        AssertEx.True(string.IsNullOrEmpty(docker.LastUtilityRunSpec!.NetworkName), "recommend must run with no network.");
    }

    [Test]
    public async Task RunModelFitUtility_RecommendArgv_OmitsUseCaseWhenEmpty()
    {
        var (service, docker) = CreateService();
        var request = RecommendRequest();
        request.UseCase = string.Empty;

        await service.RunModelFitUtility(request, Context());

        AssertEx.False(docker.LastUtilityRunSpec!.Arguments.Contains("--use-case"), "an empty use-case must not be appended.");
    }

    [Test]
    public async Task RunModelFitUtility_RecommendArgv_ClampsLimitToMax()
    {
        var (service, docker) = CreateService(new ModelFitUtilityOptions { MaxRecommendLimit = 10 });
        var request = RecommendRequest();
        request.Limit = 999;

        await service.RunModelFitUtility(request, Context());

        var argv = docker.LastUtilityRunSpec!.Arguments;
        var limitIndex = argv.ToList().IndexOf("--limit");
        AssertEx.True(limitIndex >= 0, "the limit flag must be present.");
        AssertEx.Equal("10", argv[limitIndex + 1]);
    }

    [Test]
    public async Task RunModelFitUtility_BenchmarkArgv_BuildsBenchCommandWithRuntimeNetworkAndOllamaHost()
    {
        var (service, docker) = CreateService();
        docker.ScriptUtilityRun(0, "{}");

        var reply = await service.RunModelFitUtility(new RunModelFitUtilityRequest
        {
            ImageReference = AllowedReference,
            Operation = ModelFitOperationMessage.ModelFitOperationBenchmark,
            ModelName = "llama3",
            ProviderName = "ollama",
            ProviderUrl = "http://ollama:11434",
            Network = ModelFitNetworkModeMessage.ModelFitNetworkModeRuntime
        }, Context());

        AssertEx.Equal(ModelFitTerminalStatusMessage.Succeeded, reply.Status);
        var spec = docker.LastUtilityRunSpec!;
        AssertEx.Equal("bench", spec.Arguments[0]);
        AssertEx.Equal("--provider", spec.Arguments[1]);
        AssertEx.Equal("ollama", spec.Arguments[2]);
        AssertEx.Equal("--url", spec.Arguments[3]);
        AssertEx.Equal("http://ollama:11434", spec.Arguments[4]);
        AssertEx.Equal("--json", spec.Arguments[5]);
        AssertEx.Equal("llama3", spec.Arguments[6]);
        // Benchmark attaches the managed runtime network and sets OLLAMA_HOST as a belt-and-suspenders.
        AssertEx.Equal("xe-engine-net", spec.NetworkName);
        AssertEx.Equal("http://ollama:11434", spec.Environment["OLLAMA_HOST"]);
    }

    [Test]
    public async Task RunModelFitUtility_BenchmarkWithoutModelName_RejectedWithoutRunning()
    {
        var (service, docker) = CreateService();

        var reply = await service.RunModelFitUtility(new RunModelFitUtilityRequest
        {
            ImageReference = AllowedReference,
            Operation = ModelFitOperationMessage.ModelFitOperationBenchmark,
            ModelName = string.Empty,
            ProviderName = "ollama",
            Network = ModelFitNetworkModeMessage.ModelFitNetworkModeRuntime
        }, Context());

        AssertEx.Equal(ModelFitTerminalStatusMessage.Failed, reply.Status);
        AssertEx.Equal(0, docker.UtilityRunCount);
        AssertEx.NotEmpty(reply.SanitizedError);
    }

    [Test]
    public async Task RunModelFitUtility_WhenRepositoryNotAllowlisted_RejectedWithoutRunning()
    {
        var (service, docker) = CreateService();
        var request = RecommendRequest();
        request.ImageReference = "docker.io/library/alpine:3.20@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";

        var reply = await service.RunModelFitUtility(request, Context());

        AssertEx.Equal(ModelFitTerminalStatusMessage.Failed, reply.Status);
        AssertEx.Equal(0, docker.UtilityRunCount);
        AssertEx.Equal("image reference rejected", reply.SanitizedError);
    }

    [Test]
    [Arguments("ghcr.io/alexsjones/llmfit:latest@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c")]
    [Arguments("ghcr.io/alexsjones/llmfit:0.9.30")]
    [Arguments("not a reference")]
    public async Task RunModelFitUtility_WhenReferenceIsNotCanonical_RejectedWithoutRunning(string reference)
    {
        var (service, docker) = CreateService();
        var request = RecommendRequest();
        request.ImageReference = reference;

        var reply = await service.RunModelFitUtility(request, Context());

        AssertEx.Equal(ModelFitTerminalStatusMessage.Failed, reply.Status);
        AssertEx.Equal(0, docker.UtilityRunCount);
    }

    [Test]
    public async Task RunModelFitUtility_WhenCancelled_ReturnsCancelledAndRemovesContainer()
    {
        var (service, docker) = CreateService();
        docker.ScriptBlockingUtilityRun();

        using var cts = new CancellationTokenSource();
        var runTask = service.RunModelFitUtility(RecommendRequest(), Context(cts.Token));
        await cts.CancelAsync();
        var reply = await runTask;

        AssertEx.Equal(ModelFitTerminalStatusMessage.Cancelled, reply.Status);
        AssertEx.False(reply.Completed);
        AssertEx.Equal(-1, reply.ExitCode);
        // The utility container is removed on cancellation (debug retention is off by default).
        AssertEx.True(docker.LastUtilityContainerRemoved, "a cancelled run must remove its container.");
    }

    [Test]
    public async Task RunModelFitUtility_WhenTimeoutElapses_ReturnsTimedOutAndRemovesContainer()
    {
        var (service, docker) = CreateService(new ModelFitUtilityOptions { DefaultMaxRuntimeSeconds = 600 });
        docker.ScriptBlockingUtilityRun();

        // A tiny per-request timeout drives the timeout branch (distinct from a caller cancel).
        var request = RecommendRequest();
        request.TimeoutSeconds = 1;

        // The blocking fake run honours the linked timeout token; the service maps the timeout CTS to TIMED_OUT.
        var reply = await service.RunModelFitUtility(request, Context());

        AssertEx.Equal(ModelFitTerminalStatusMessage.TimedOut, reply.Status);
        AssertEx.False(reply.Completed);
        AssertEx.True(docker.LastUtilityContainerRemoved, "a timed-out run must remove its container.");
    }

    [Test]
    public async Task RunModelFitUtility_WhenFailedAndRetainDebugOff_RemovesContainer()
    {
        var (service, docker) = CreateService(new ModelFitUtilityOptions { RetainFailedContainersForDebug = false });
        docker.ScriptUtilityRun(1, string.Empty, "Error: provider unavailable");

        var reply = await service.RunModelFitUtility(RecommendRequest(), Context());

        AssertEx.Equal(ModelFitTerminalStatusMessage.Failed, reply.Status);
        AssertEx.Equal(1, reply.ExitCode);
        AssertEx.True(docker.LastUtilityContainerRemoved, "a failed run removes its container when debug retention is off.");
        // The sanitized error never echoes raw stderr or secrets.
        AssertEx.False(reply.SanitizedError.Contains("provider unavailable", StringComparison.Ordinal), "sanitized error must not echo stderr.");
    }

    [Test]
    public async Task RunModelFitUtility_WhenFailedAndRetainDebugOn_KeepsContainer()
    {
        var (service, docker) = CreateService(new ModelFitUtilityOptions { RetainFailedContainersForDebug = true });
        docker.ScriptUtilityRun(2, string.Empty, "error: bad arg");

        var reply = await service.RunModelFitUtility(RecommendRequest(), Context());

        AssertEx.Equal(ModelFitTerminalStatusMessage.Failed, reply.Status);
        AssertEx.False(docker.LastUtilityContainerRemoved, "a failed run keeps its container only when the debug option is on.");
        // The spec carries the retain flag the docker client honours.
        AssertEx.True(docker.LastUtilityRunSpec!.RetainOnFailure);
    }

    [Test]
    public void RunModelFitUtilityRequest_HasNoArbitraryCommandOrImageNameField()
    {
        // The narrow contract must never expose a command/argv/executable/image-name field — the argv is built
        // server-side. Asserting on the generated proto type pins this invariant: only the intent fields exist.
        var properties = typeof(RunModelFitUtilityRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        foreach (var forbidden in new[] { "Command", "Arguments", "Argv", "Executable", "ImageName", "Cmd", "Shell" })
        {
            AssertEx.False(properties.Contains(forbidden), $"the request must not expose a '{forbidden}' field.");
        }

        // The only image field is the pinned reference the HostAgent re-validates.
        AssertEx.True(properties.Contains("ImageReference"), "the request carries only a pinned image reference.");
    }

    private static ServerCallContext Context(CancellationToken cancellationToken = default)
    {
        return new TestServerCallContext(cancellationToken);
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly CancellationToken _cancellationToken;

        public TestServerCallContext(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        protected override string MethodCore => "/xe.hostagent.v1.ModelFitUtilityControl/Test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(string.Empty, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
        {
            throw new NotSupportedException();
        }

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
        {
            return Task.CompletedTask;
        }
    }
}
