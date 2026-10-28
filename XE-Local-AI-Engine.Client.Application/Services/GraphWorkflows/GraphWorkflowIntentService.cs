namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Resolves a command to one graph workflow that accepts <c>Intent</c> starts and starts it. See
///     docs/wiki/21-graph-workflows.md ("Intent routing").
/// </summary>
/// <remarks>
///     An exact alias or name match wins; otherwise the operator's router graph (node setting
///     <c>IntentRouterDefinitionId</c>) picks a name or <c>unknown</c>. There is no confidence threshold (ADR 0021): the
///     controls are the alias, the router's <c>unknown</c> label and the user's Enter.
/// </remarks>
public sealed class GraphWorkflowIntentService
{
    /// <summary>The router label that means "none of these".</summary>
    public const string UnknownChoice = "unknown";

    /// <summary>How far ahead of this node's clock an utterance may be stamped before it is refused.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);

    // Fixed poll interval, like the scheduler's run-graph handler; subscribe if a service-side run-event seam appears.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly byte[] RouterRequestSalt = "xe-intent-router:"u8.ToArray();

    private readonly IGraphWorkflowDefinitionService _definitions;

    private readonly IOptions<GraphWorkflowOptions> _options;

    private readonly IGraphWorkflowRunService _runs;

    private readonly INodeRuntimeSettings _settings;

    private readonly TimeProvider _timeProvider;

    public GraphWorkflowIntentService(IGraphWorkflowDefinitionService definitions,
        IGraphWorkflowRunService runs,
        INodeRuntimeSettings settings,
        IOptions<GraphWorkflowOptions> options,
        TimeProvider timeProvider)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Collapses whitespace runs, trims and strips trailing <c>.!?</c>: the one form aliases match in, compared ignoring case.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.TrimEnd('.', '!', '?', ' ');
    }

    /// <summary>
    ///     The router run's request id: the first 16 bytes of SHA-256 over a fixed salt and <paramref name="requestId" />,
    ///     stamped as a version-5-style GUID. Deterministic, so a retried command replays the same router run, and
    ///     never equal to the command's own id.
    /// </summary>
    public static Guid RouterRequestId(Guid requestId)
    {
        var buffer = new byte[RouterRequestSalt.Length + 16];
        RouterRequestSalt.CopyTo(buffer, 0);
        requestId.TryWriteBytes(buffer.AsSpan(RouterRequestSalt.Length));
        var hash = SHA256.HashData(buffer);
        hash[7] = (byte)((hash[7] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    public async Task<GraphWorkflowIntentResult> ResolveAndStartAsync(GraphWorkflowIntentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var utterance = Normalize(request.Utterance ?? string.Empty);
        if (utterance.Length == 0)
        {
            throw Invalid("utterance", "An intent needs an utterance with words in it.");
        }

        if (request.RequestId == Guid.Empty)
        {
            throw Invalid("requestId", "An intent needs a caller-minted request id.");
        }

        var now = _timeProvider.GetUtcNow();
        if (request.UtteredAt - now > ClockSkew)
        {
            throw Invalid("utteredAt", "The utterance is stamped in the future; check the sending device's clock.");
        }

        var maxAge = await _settings.GetIntentMaxUtteranceAgeAsync(cancellationToken);
        if (now - request.UtteredAt > maxAge)
        {
            throw new GraphWorkflowIntentExpiredException(string.Create(CultureInfo.InvariantCulture,
                $"The command is older than the {maxAge.TotalSeconds:0} s this node accepts, so it did not run. Say it again."));
        }

        var routerId = await _settings.GetIntentRouterDefinitionIdAsync(cancellationToken);
        var summaries = await _definitions.ListAsync(cancellationToken);
        var candidates = await LoadCandidatesAsync(summaries, routerId, cancellationToken);
        var origin = new GraphWorkflowRunOrigin
        {
            Source = GraphWorkflowTriggerSource.Intent,
            PrincipalId = request.PrincipalId
        };

        var matches = MatchesOf(candidates, utterance);
        if (matches.Count > 0)
        {
            return await StartMatchAsync(matches, request, origin, GraphWorkflowIntentMatch.Alias, cancellationToken);
        }

        if (routerId is not { } id)
        {
            return NotMatched(GraphWorkflowIntentMatch.None, "No workflow name or alias matches, and no intent router is set.");
        }

        var router = summaries.FirstOrDefault(summary => summary.Id == id);
        if (router is null || !router.TriggerSources.HasFlag(GraphWorkflowTriggerSource.Intent))
        {
            return NotMatched(GraphWorkflowIntentMatch.None, "The intent router workflow is missing or does not accept Intent starts.");
        }

        var answer = await AskRouterAsync(router, request, candidates, origin, cancellationToken);
        if (answer.Choice is not { } choice)
        {
            return NotMatched(GraphWorkflowIntentMatch.Router, answer.Reason);
        }

        if (string.Equals(choice.Trim(), UnknownChoice, StringComparison.OrdinalIgnoreCase))
        {
            return NotMatched(GraphWorkflowIntentMatch.Router, "The intent router answered unknown.");
        }

        var routed = MatchesOf(candidates, Normalize(choice));
        return routed.Count == 0
            ? NotMatched(GraphWorkflowIntentMatch.Router, "The intent router chose a name no workflow open to intents carries.")
            : await StartMatchAsync(routed, request, origin, GraphWorkflowIntentMatch.Router, cancellationToken);
    }

    /// <summary>Every definition open to <c>Intent</c> except the router, with its name and aliases normalized.</summary>
    /// <remarks>Only those: a stale alias on a definition the operator never opened to intents must not route to it.</remarks>
    private async Task<List<IntentCandidate>> LoadCandidatesAsync(IReadOnlyList<GraphWorkflowDefinitionSummary> summaries,
        Guid? routerId,
        CancellationToken cancellationToken)
    {
        var candidates = new List<IntentCandidate>();
        foreach (var summary in summaries.Where(summary => summary.TriggerSources.HasFlag(GraphWorkflowTriggerSource.Intent) && summary.Id != routerId))
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Normalize(summary.Name)
            };
            try
            {
                var definition = await _definitions.GetAsync(summary.Id, cancellationToken);
                var start = GraphWorkflowGraph.Parse(definition.GraphJson).Nodes.Values.Select(static node => node.Config).OfType<GraphWorkflowStartConfig>().Single();
                keys.UnionWith(start.Aliases.Select(Normalize));
            }
            catch (GraphWorkflowNotFoundException)
            {
                // Deleted since the list was read.
                continue;
            }
            catch (GraphWorkflowValidationException)
            {
                // A stored graph this build no longer parses still answers to its name; the start will report why it cannot run.
            }

            candidates.Add(new IntentCandidate
            {
                Id = summary.Id,
                Name = summary.Name,
                Keys = keys
            });
        }

        return candidates;
    }

    private static List<IntentCandidate> MatchesOf(List<IntentCandidate> candidates, string normalized) =>
        candidates.Where(candidate => candidate.Keys.Contains(normalized)).ToList();

    private async Task<GraphWorkflowIntentResult> StartMatchAsync(List<IntentCandidate> matches,
        GraphWorkflowIntentRequest request,
        GraphWorkflowRunOrigin origin,
        GraphWorkflowIntentMatch matchedBy,
        CancellationToken cancellationToken)
    {
        if (matches.Count > 1)
        {
            return NotMatched(matchedBy, $"The command matches {matches.Count} workflows ({string.Join(", ", matches.Select(static match => $"'{match.Name}'"))}); give one of them a distinct alias.");
        }

        var match = matches[0];
        var run = await _runs.StartAsync(match.Id, request.RequestId, inputJson: null, definitionVersion: null, origin, cancellationToken);
        return new GraphWorkflowIntentResult
        {
            Status = GraphWorkflowIntentStatus.Started,
            MatchedBy = matchedBy,
            DefinitionId = match.Id,
            DefinitionName = match.Name,
            Run = run
        };
    }

    /// <summary>
    ///     Starts the router run on <c>{ "utterance", "choices" }</c> and waits for it on the clock, cancelling it once at
    ///     <see cref="GraphWorkflowOptions.IntentRouterTimeoutSeconds" />.
    /// </summary>
    /// <remarks>
    ///     A dropped request does not cancel the run: a retry with the same request id waits on the same run instead.
    /// </remarks>
    private async Task<RouterAnswer> AskRouterAsync(GraphWorkflowDefinitionSummary router,
        GraphWorkflowIntentRequest request,
        List<IntentCandidate> candidates,
        GraphWorkflowRunOrigin origin,
        CancellationToken cancellationToken)
    {
        var input = new JsonObject
        {
            ["utterance"] = request.Utterance.Trim(),
            ["choices"] = new JsonArray([.. candidates.Select(static candidate => (JsonNode?)JsonValue.Create(candidate.Name))])
        }.ToJsonString();

        GraphWorkflowRunDetail detail;
        try
        {
            detail = await _runs.StartAsync(router.Id, RouterRequestId(request.RequestId), input, definitionVersion: null, origin, cancellationToken);
        }
        catch (Exception exception) when (exception is GraphWorkflowValidationException or GraphWorkflowRunConflictException or GraphWorkflowNotFoundException)
        {
            return RouterAnswer.Failed($"The intent router could not start: {exception.Message}");
        }

        var timeoutSeconds = _options.Value.IntentRouterTimeoutSeconds;
        var deadline = _timeProvider.GetUtcNow().AddSeconds(timeoutSeconds);
        while (!GraphWorkflowStateMachine.IsTerminal(detail.Run.Status))
        {
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                await CancelQuietlyAsync(detail.Run.Id);
                return RouterAnswer.Failed(string.Create(CultureInfo.InvariantCulture, $"The intent router did not answer within {timeoutSeconds} s and was cancelled."));
            }

            await Task.Delay(PollInterval, _timeProvider, cancellationToken);
            detail = await _runs.GetRunAsync(detail.Run.Id, cancellationToken);
        }

        if (detail.Run.Status != GraphWorkflowRunStatus.Completed)
        {
            return RouterAnswer.Failed($"The intent router run ended {detail.Run.Status} ({detail.Run.FailureClass}).");
        }

        return ReadChoice(detail.Run.OutputJson) is { } choice
            ? new RouterAnswer
            {
                Choice = choice
            }
            : RouterAnswer.Failed("The intent router's result carries no choice.");
    }

    /// <summary>The router's result: a bare string, or an object with a string <c>choice</c> (End <c>resultPath: "input.output"</c>).</summary>
    private static string? ReadChoice(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            var root = document.RootElement;
            var value = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choice", out var choice) ? choice : root;
            return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task CancelQuietlyAsync(Guid runId)
    {
        try
        {
            await _runs.CancelAsync(runId, CancellationToken.None);
        }
        catch (GraphWorkflowRunConflictException)
        {
            // It ended between the last read and the cancel.
        }
    }

    private static GraphWorkflowValidationException Invalid(string key, string message) =>
        new(GraphWorkflowValidationResult.Invalid([new GraphWorkflowValidationError(key, message)]));

    private static GraphWorkflowIntentResult NotMatched(GraphWorkflowIntentMatch matchedBy, string? reason) =>
        new()
        {
            Status = GraphWorkflowIntentStatus.NotMatched,
            MatchedBy = matchedBy,
            Reason = reason
        };

    private sealed class IntentCandidate
    {
        public required Guid Id { get; init; }

        public required string Name { get; init; }

        public required IReadOnlySet<string> Keys { get; init; }
    }

    private sealed class RouterAnswer
    {
        public string? Choice { get; init; }

        public string? Reason { get; init; }

        public static RouterAnswer Failed(string reason) =>
            new()
            {
                Reason = reason
            };
    }
}
