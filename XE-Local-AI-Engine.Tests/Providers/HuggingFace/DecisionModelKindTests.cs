namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A native decision model is told apart by its GGUF header, never its name: <c>&lt;arch&gt;.decision.type</c> is read
///     off the file and classifies <see cref="ModelKind.Decision" /> ahead of every name rule and the Chat default.
/// </summary>
/// <remarks>
///     The fixtures carry the keys the three catalog models ship: Julia-1 and Laya are <c>modern-bert</c> with
///     <c>laya</c>, Kev-4B is a <c>qwen35</c> with <c>kev</c> AND a tool-calling chat template, which is the case a
///     Chat default would get wrong.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class DecisionModelKindTests
{
    [Test]
    [Arguments("modern-bert", "laya", "ggml-org/Julia-1-GGUF:Q8_0")]
    [Arguments("modern-bert", "laya", "ggml-org/Laya-GGUF:Q8_0")]
    [Arguments("qwen35", "kev", "ggml-org/Kev-4B-GGUF:Q4_K_M")]
    public async Task AHeaderDecisionType_ClassifiesDecision_WhateverTheNameSays(string architecture, string decisionType, string modelName)
    {
        var metadata = await ReadAsync(new GgufHeaderBytesBuilder()
                                       .WithString("general.architecture", architecture)
                                       .WithUint32($"{architecture}.context_length", value: 8192)
                                       .WithString($"{architecture}.decision.type", decisionType)
                                       .WithString("tokenizer.chat_template", "{% for tool in tools %}{{ tool }}{% endfor %}")
                                       .Build());

        AssertEx.Equal(decisionType, metadata.DecisionType);
        AssertEx.Equal(ModelKind.Decision, LocalGgufModelKindClassifier.Classify(modelName, metadata.DecisionType));
        AssertEx.Equal(ModelKind.Chat, LocalGgufModelKindClassifier.Classify(modelName), "without the header fact the name rules would call it chat.");
    }

    [Test]
    public async Task AChatHeaderWithoutTheKey_StaysChat()
    {
        var metadata = await ReadAsync(new GgufHeaderBytesBuilder()
                                       .WithString("general.architecture", "qwen35")
                                       .WithUint32("qwen35.context_length", value: 262144)
                                       .Build());

        AssertEx.Null(metadata.DecisionType);
        AssertEx.Equal(ModelKind.Chat, LocalGgufModelKindClassifier.Classify("unsloth/Qwen3.5-4B-GGUF:Q4_K_M", metadata.DecisionType));
    }

    [Test]
    public async Task ABlankDecisionType_CountsAsAbsent()
    {
        var metadata = await ReadAsync(new GgufHeaderBytesBuilder()
                                       .WithString("general.architecture", "modern-bert")
                                       .WithString("modern-bert.decision.type", "  ")
                                       .Build());

        AssertEx.Null(metadata.DecisionType);
    }

    [Test]
    public void ADecisionModel_IsNeverChat_EvenWhenTheOperatorOverridesItToChat()
    {
        var overridden = new ModelClassificationRecord
        {
            ModelName = "ggml-org/Kev-4B-GGUF:Q4_K_M",
            Digest = null,
            DetectedKind = ModelKind.Chat,
            DetectedCapabilitiesJson = null,
            OverrideKind = ModelKind.Chat,
            DetectedAtUtc = null,
            UpdatedAtUtc = 0
        };

        AssertEx.False(LocalGgufModelKindClassifier.IsChatModel(overridden.ModelName, overridden, decisionType: "kev"),
            "llama-server serves the file as a decision model whatever the override says.");
        AssertEx.True(LocalGgufModelKindClassifier.IsChatModel(overridden.ModelName, overridden), "without the header fact the override decides, as before.");
        AssertEx.False(LocalGgufModelKindClassifier.IsChatModel("x/y-GGUF:Q8_0", classification: null, decisionType: "laya"));
    }

    [Test]
    public void TheModelList_ShowsADecisionModelAsDecisionWithNoChatCapabilities()
    {
        var responses = LocalModelsMapper.ToLlamaCppModelResponses([
            new LocalModelDescriptor
            {
                ModelName = "ggml-org/Kev-4B-GGUF:Q4_K_M",
                ProviderName = "llamacpp",
                IsAvailable = true,
                SizeBytes = 3_033_489_824,
                ModifiedAt = DateTimeOffset.UnixEpoch,
                MaxContextTokens = 262144,
                IsToolCapable = true,
                IsReasoningCapable = true,
                Capabilities = ["completion", "tools", "thinking"],
                DecisionType = "kev"
            }
        ], selectedModelName: null);

        var kev = responses.Single();
        AssertEx.Equal(ModelKind.Decision.ToString(), kev.Kind, "the picker offers Chat only, so Decision keeps it out.");
        AssertEx.Equal(ModelKind.Decision.ToString(), kev.DetectedKind);
        AssertEx.Empty(kev.Capabilities, "a decision head generates no text, whatever its base model's template advertises.");
        AssertEx.False(kev.IsToolCapable);
        AssertEx.False(kev.IsReasoningCapable);
    }

    private static async Task<GgufHeaderMetadata> ReadAsync(byte[] header)
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("decision.gguf");
        await File.WriteAllBytesAsync(path, header);
        var options = new HuggingFaceOptions();
#pragma warning disable CA2000 // The reader retains this client; its throwing in-memory handler owns no sockets.
        var http = new HttpClient(new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) =>
            throw new InvalidOperationException("A local header read must never touch the HTTP client.")));
#pragma warning restore CA2000
        var reader = new GgufHeaderReader(http, GgufStoreTestInfrastructure.NoTokenStore(), options, NullLogger<GgufHeaderReader>.Instance, TimeProvider.System);
        return await reader.ReadHeaderFromFileAsync(path, CancellationToken.None);
    }
}
