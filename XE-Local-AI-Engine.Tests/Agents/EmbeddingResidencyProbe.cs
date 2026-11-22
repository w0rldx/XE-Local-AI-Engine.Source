namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The real tool-relevance selector and playbook ranker over a provider that records the residency intent of every
///     embedding generator, so an origin test sees which intent its flow carries into both ranking paths.
/// </summary>
/// <remarks>Every embedding call fails with a transport error, so both degrade to lexical: only the intent is observed.</remarks>
internal sealed class EmbeddingResidencyProbe
{
    private const string Model = "probe-embed";
    private const string ProviderKey = "probe";
    private const string Query = "summarize the error logs";

    private readonly Lock _gate = new();
    private readonly List<ModelResidencyIntent> _intents = [];
    private readonly EmbeddingPlaybookRetrievalRanker _ranker;
    private readonly EmbeddingToolRelevanceSelector _selector;

    public EmbeddingResidencyProbe()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
                 .Returns(_ => Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new HttpRequestException("probe transport failure")));
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(ProviderKey);
        provider.CreateEmbeddingGenerator(Arg.Do<LocalModelSelection>(Record)).Returns(generator);
        var resolver = SingleProviderResolverFactory.Create(provider);

        var toolOptions = Options.Create(new ToolRelevanceOptions
        {
            EmbeddingModelName = Model,
            EmbeddingProviderName = ProviderKey
        });
        _selector = new EmbeddingToolRelevanceSelector(resolver,
            toolOptions,
            new LexicalToolRelevanceSelector(toolOptions),
            NullLogger<EmbeddingToolRelevanceSelector>.Instance,
            TimeProvider.System);
        _ranker = new EmbeddingPlaybookRetrievalRanker(resolver,
            Options.Create(new PlaybookRetrievalOptions
            {
                EmbeddingModelName = Model,
                EmbeddingProviderName = ProviderKey
            }),
            new LexicalPlaybookRetrievalRanker(),
            NullLogger<EmbeddingPlaybookRetrievalRanker>.Instance,
            TimeProvider.System);
    }

    /// <summary>The intent of every generator created, in creation order: the tool selection's, then the ranking's, per call.</summary>
    public IReadOnlyList<ModelResidencyIntent> Intents
    {
        get
        {
            lock (_gate)
            {
                return _intents.ToArray();
            }
        }
    }

    /// <summary>One tool-relevance selection, then one playbook ranking, both in the caller's async flow.</summary>
    public async Task RankBothAsync()
    {
        _ = await _selector.SelectAsync(Query, [Candidate("read_logs"), Candidate("write_report")], threshold: 1, CancellationToken.None);
        _ = await _ranker.SelectTopKAsync(Query, [Action()], k: 1, CancellationToken.None);
    }

    private static ToolRelevanceCandidate Candidate(string name)
    {
        return new ToolRelevanceCandidate
        {
            Name = name,
            Description = name,
            IsCore = false
        };
    }

    private static PlaybookActionRecord Action()
    {
        return new PlaybookActionRecord
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            State = PlaybookActionState.Enabled,
            Source = PlaybookActionSource.Manual,
            TriggerCondition = "error logs",
            Behavior = "behaviour text",
            Scope = null,
            Priority = 10,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1
        };
    }

    private void Record(LocalModelSelection selection)
    {
        lock (_gate)
        {
            _intents.Add(selection.ResidencyIntent);
        }
    }
}
