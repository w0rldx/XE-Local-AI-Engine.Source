namespace XE_Local_AI_Engine.Tests.Integration;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Opt-in hardware compatibility lane for the upgraded MAF/MEAI stack. Unlike the permanent deterministic
///     release gate, this test requires an operator-supplied llama-server executable and fixed local GGUF.
/// </summary>
public sealed class AgentFrameworkHardwareCompatibilityTests
{
    private const string ModelName = "framework-compatibility/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M";
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

    [Test]
    public async Task ProductionInvocationRunner_CompletesThroughLlamaServerMeaiAndMaf()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_AGENT_FRAMEWORK_HARDWARE_COMPAT"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip.Test("Run scripts/run-agent-framework-hardware-compat.sh with a fixed GGUF and llama-server.");
        }

        var modelPath = RequireFile("XE_FRAMEWORK_COMPAT_GGUF_PATH");
        var serverPath = RequireFile("XE_LLAMACPP_SERVER_PATH");
        var evidencePath = Environment.GetEnvironmentVariable("XE_FRAMEWORK_COMPAT_EVIDENCE_PATH")
                           ?? throw new InvalidOperationException("XE_FRAMEWORK_COMPAT_EVIDENCE_PATH is required.");
        var variant = Environment.GetEnvironmentVariable("XE_LLAMACPP_VARIANT") ?? "cpu";
        var modelFile = new FileInfo(modelPath);
        var modelSha256 = await HashFileAsync(modelPath).ConfigureAwait(false);
        var serverSha256 = await HashFileAsync(serverPath).ConfigureAwait(false);
        var store = new FixedGgufModelStore(modelPath, modelFile.Length, modelSha256);

        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IGgufModelStore>();
                services.AddSingleton<IGgufModelStore>(store);
            }
        };

        var resolver = factory.Services.GetRequiredService<ILocalModelProviderResolver>();
        var provider = await resolver.ResolveProviderForModelAsync(ModelName).ConfigureAwait(false);
        var agentFactory = factory.Services.GetRequiredService<IInvocationAgentFactory>();
        var runner = factory.Services.GetRequiredService<IInvocationRunner>();
        var dispatcher = factory.Services.GetRequiredService<IWorkerEventDispatcher>();

        AssertEx.True(resolver is LocalModelProviderResolver, "The production model-provider resolver must be active.");
        AssertEx.True(provider is LlamaServerLocalModelProvider, "The resolved provider must be the production llama-server provider.");
        AssertEx.True(runner is InvocationRunner, "The production InvocationRunner must execute the turn.");
        AssertEx.Contains(agentFactory.GetType().FullName ?? string.Empty,
            "InvocationAgentFactory",
            StringComparison.Ordinal,
            "The production MAF invocation-agent factory must build the agent.");

        var package = RuntimePackageBuilder.Valid()
                                           .WithModel(ModelName)
                                           .WithSystemPrompt("Follow the user instruction exactly.")
                                           .WithUserMessage("Reply with exactly FRAMEWORK_COMPAT_OK and nothing else.")
                                           .WithRequestedCapability(LocalChatLoopbackDefaults.RequestedCapability)
                                           .WithTimeout(invocationSeconds: 600, streamIdleSeconds: 180)
                                           .Build();

        try
        {
            await using var invocationLease = await dispatcher.ReportInvocationAssignedAsync(package).ConfigureAwait(false);
            using var context = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
            await runner.RunAsync(context).ConfigureAwait(false);

            var state = AssertEx.NotNull(dispatcher.CurrentInvocation);
            AssertEx.Equal(InvocationStatus.Completed, state.Status, state.Error);
            AssertEx.NotNullOrEmpty(state.StreamedContent);
            AssertEx.Contains(state.StreamedContent,
                "FRAMEWORK_COMPAT_OK",
                StringComparison.Ordinal,
                "The fixed compatibility prompt must survive the provider/MEAI/MAF/InvocationRunner stack.");

            await WriteEvidenceAsync(evidencePath,
                    new EvidenceInput(new EvidenceFile(modelFile.Length, modelSha256),
                        serverSha256,
                        variant,
                        state.StreamedContent,
                        new EvidencePath(resolver.GetType().FullName,
                            provider.GetType().FullName,
                            agentFactory.GetType().FullName,
                            runner.GetType().FullName)))
                .ConfigureAwait(false);
        }
        finally
        {
            await provider.UnloadModelAsync(ModelName, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string RequireFile(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException($"{variable} must name an existing file.");
        }

        return Path.GetFullPath(path);
    }

    private static async Task WriteEvidenceAsync(string evidencePath, EvidenceInput input)
    {
        var payload = new
        {
            schemaVersion = 1,
            capturedAtUtc = DateTimeOffset.UtcNow,
            sourceCommit = ResolveSourceCommit(),
            result = "passed",
            backend = input.Variant,
            model = new
            {
                sha256 = input.Model.Sha256,
                sizeBytes = input.Model.SizeBytes
            },
            llamaServer = new
            {
                sha256 = input.ServerSha256
            },
            path = new
            {
                resolver = input.Path.ResolverType,
                provider = input.Path.ProviderType,
                meaiAdapter = "OpenAI.Chat.ChatClient.AsIChatClient",
                mafFactory = input.Path.AgentFactoryType,
                runner = input.Path.RunnerType
            },
            response = new
            {
                utf8Bytes = Encoding.UTF8.GetByteCount(input.Response),
                sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Response)))
            }
        };

        var fullPath = Path.GetFullPath(evidencePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
                                  ?? throw new InvalidOperationException("Evidence path has no parent directory."));
        await File.WriteAllTextAsync(fullPath,
                JsonSerializer.Serialize(payload, EvidenceJsonOptions) + Environment.NewLine)
            .ConfigureAwait(false);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string ResolveSourceCommit()
    {
        return typeof(AgentFrameworkHardwareCompatibilityTests).Assembly
                                                               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                                               ?.InformationalVersion
               ?? "unknown";
    }

    private sealed record EvidenceFile(long SizeBytes, string Sha256);

    private sealed record EvidencePath(
        string? ResolverType,
        string? ProviderType,
        string? AgentFactoryType,
        string? RunnerType);

    private sealed record EvidenceInput(
        EvidenceFile Model,
        string ServerSha256,
        string Variant,
        string Response,
        EvidencePath Path);

    private sealed class FixedGgufModelStore(string modelPath, long sizeBytes, string contentIdentity) : IGgufModelStore
    {
        private readonly GgufModelFootprintFacts _facts = new(
            Quant: "Q4_K_M",
            FileSizeBytes: sizeBytes,
            ParamCount: null,
            BlockCount: null,
            AttentionHeadCount: null,
            AttentionHeadCountKV: null,
            EmbeddingLength: null,
            ContextLength: 32_768,
            ContentIdentity: contentIdentity);

        public Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult<string?>(Matches(modelName) ? modelPath : null);
        }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
        {
            IReadOnlyList<LocalModelDescriptor> result =
            [
                new()
                {
                    ModelName = ModelName,
                    ProviderName = LlamaServerProviderConstants.ProviderName,
                    IsAvailable = true,
                    SizeBytes = sizeBytes,
                    ModifiedAt = File.GetLastWriteTimeUtc(modelPath),
                    MaxContextTokens = 32_768,
                    IsToolCapable = false,
                    IsReasoningCapable = false,
                    Capabilities = ["completion"]
                }
            ];
            return Task.FromResult(result);
        }

        public Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct)
        {
            return Task.FromResult(ModelName);
        }

        public Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            throw new NotSupportedException("The compatibility lane uses an already-present fixed GGUF.");
        }

        public Task DeleteModelAsync(string modelName, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult(Matches(modelName));
        }

        public Task<GgufModelFootprintFacts?> ResolveModelFootprintFactsAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult<GgufModelFootprintFacts?>(Matches(modelName) ? _facts : null);
        }

        private static bool Matches(string modelName)
        {
            return string.Equals(modelName, ModelName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
