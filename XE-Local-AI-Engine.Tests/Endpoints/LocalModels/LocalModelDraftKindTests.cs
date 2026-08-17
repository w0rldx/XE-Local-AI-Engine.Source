namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A downloaded speculative-decoding drafter used to be classified <see cref="ModelKind.Chat" /> —
///     an installed GGUF defaults to Chat, and nothing distinguished the drafter — so it sat in the chat picker as a
///     0.4 GB twin of the 7.8 GB model it drafts for. It must classify as <see cref="ModelKind.Draft" /> (the React
///     picker offers <c>kind === "Chat"</c> only), while the real model beside it stays Chat.
/// </summary>
public sealed class LocalModelDraftKindTests
{
    [Test]
    public void ToLlamaCppModelResponses_ClassifiesTheDrafterAsDraft_AndTheRealModelAsChat()
    {
        var responses = LocalModelsMapper.ToLlamaCppModelResponses([
            Descriptor("unsloth/gemma-4-12b-it-GGUF:MTP-Q8_0", sizeBytes: 400_000_000),
            Descriptor("unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", sizeBytes: 7_800_000_000)
        ], selectedModelName: null);

        var draft = responses.Single(response => response.ModelName == "unsloth/gemma-4-12b-it-GGUF:MTP-Q8_0");
        AssertEx.Equal(ModelKind.Draft.ToString(), draft.Kind);
        AssertEx.Equal(ModelKind.Draft.ToString(), draft.DetectedKind);

        var real = responses.Single(response => response.ModelName == "unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL");
        AssertEx.Equal(ModelKind.Chat.ToString(), real.Kind);
    }

    [Test]
    public void ToLlamaCppModelResponses_LeavesAnMtpNamedBaseModelChat()
    {
        // unsloth/Qwen3.6-27B-MTP-GGUF is a real 21 GB chat model — only the QUANT marker means "draft".
        var responses = LocalModelsMapper.ToLlamaCppModelResponses([Descriptor("unsloth/Qwen3.6-27B-MTP-GGUF:Q6_K", sizeBytes: 21_300_000_000)],
            selectedModelName: null);

        AssertEx.Equal(ModelKind.Chat.ToString(), responses.Single().Kind);
    }

    private static LocalModelDescriptor Descriptor(string modelName, long sizeBytes)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = sizeBytes,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = null
        };
    }
}
