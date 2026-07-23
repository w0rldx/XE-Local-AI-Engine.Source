namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The default <see cref="IUsageRateResolver" />. Holds the operator override map (seeded once per instance from node
///     settings) over a small built-in default rate table, and gates the local runtimes to free. Construct via
///     <see cref="FromSettings" /> from the current node settings so operator edits apply on the next usage-summary read
///     (no restart) — the composition root registers a scoped factory that reads the cached node settings per request.
/// </summary>
public sealed class UsageRateResolver : IUsageRateResolver
{
    /// <summary>The free rate returned for local runtimes and unknown / unpriced (provider, model) pairs.</summary>
    private static readonly ModelRate Free = new()
    {
        InputPer1M = 0,
        OutputPer1M = 0
    };

    /// <summary>
    ///     Built-in default rates for a FEW well-known hosted models, in APPROXIMATE US dollars per 1M tokens. These are
    ///     deliberately round, ballpark figures for a first-pass estimate — NOT authoritative pricing: verify against your
    ///     provider's current price sheet, and override any model via the node-settings usage-rate editor. Keyed
    ///     case-insensitively to match the run-envelope model name. Reasoning tokens bill at the output rate.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ModelRate> DefaultRates =
        new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5"] = new()
            {
                InputPer1M = 1.25,
                OutputPer1M = 10
            },
            ["gpt-5-mini"] = new()
            {
                InputPer1M = 0.25,
                OutputPer1M = 2
            },
            ["gpt-5-codex"] = new()
            {
                InputPer1M = 1.25,
                OutputPer1M = 10
            },
            ["o3"] = new()
            {
                InputPer1M = 2,
                OutputPer1M = 8
            },
            ["gpt-4o"] = new()
            {
                InputPer1M = 2.5,
                OutputPer1M = 10
            },
            ["gpt-4o-mini"] = new()
            {
                InputPer1M = 0.15,
                OutputPer1M = 0.6
            }
        };

    private readonly IReadOnlyDictionary<string, ModelRate> _overrides;

    private UsageRateResolver(IReadOnlyDictionary<string, ModelRate> overrides)
    {
        _overrides = overrides;
    }

    /// <inheritdoc />
    public ModelRate Resolve(string provider, string modelName)
    {
        // Local runtimes never cost money regardless of any override or default — gate them first.
        if (string.Equals(provider, AgentUsageProviders.Local, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, AgentUsageProviders.Ollama, StringComparison.OrdinalIgnoreCase))
        {
            return Free;
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return Free;
        }

        // Operator override wins over the built-in default; an unknown model is unpriced (free/zero).
        if (_overrides.TryGetValue(modelName, out var over))
        {
            return over;
        }

        return DefaultRates.GetValueOrDefault(modelName, Free);
    }

    /// <summary>
    ///     Builds a resolver from the persisted operator override. Rebuilds the override map with a case-insensitive
    ///     comparer (a JSON round-trip loses the store's comparer) and defensively skips blank keys / invalid rates — the
    ///     store's <c>Normalize</c> is the authority, this is belt-and-suspenders. A <see langword="null" /> / empty
    ///     override yields a resolver that uses the default table alone.
    /// </summary>
    public static UsageRateResolver FromSettings(NodeUsageRateSettings? settings)
    {
        var overrides = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
        if (settings?.Models is { } models)
        {
            foreach (var (name, rate) in models)
            {
                if (!string.IsNullOrWhiteSpace(name) && rate is not null && rate.HasValidRates)
                {
                    overrides[name.Trim()] = rate;
                }
            }
        }

        return new UsageRateResolver(overrides);
    }
}
