namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Globalization;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;

/// <summary>
///     Composes the objective a <see cref="DevWorkflowAgentExecutor" /> session is asked to meet: the node's
///     instructions, the scoped rule sets, what was asked and the upstream artifacts, inside one character budget.
/// </summary>
internal sealed class DevWorkflowObjectiveComposer
{
    /// <summary>The ceiling this composition holds itself to, in characters.</summary>
    /// <remarks>
    ///     Coupled to <c>WorkSessionService.MaxObjectiveLength</c> (8000), which is private to that class: that layer
    ///     REFUSES an over-long objective rather than trimming it, and the refusal blocks the node run for a human, so
    ///     an objective composed here must never approach it. The margin absorbs a modest reduction there, and a test
    ///     fails if the two ever cross. Only the ARTIFACT phase is bounded by it — instructions and inputs are
    ///     appended first and uncapped, since trimming what an author wrote is a worse answer than the block.
    /// </remarks>
    internal const int MaxObjectiveCharacters = 7000;

    /// <summary>The largest artifact whose bytes are worth reading to fill an objective, in bytes.</summary>
    /// <remarks>
    ///     Two orders of magnitude above anything <see cref="MaxObjectiveCharacters" /> could use, and two below the
    ///     64 MiB an artifact may be: the point is not the smallest workable number but keeping a node with several
    ///     large upstream artifacts from reading — and decoding — all of them to keep a few thousand characters of
    ///     the first.
    /// </remarks>
    private const int MaxInjectableArtifactBytes = 256 * 1024;

    private readonly IDevWorkflowArtifactBlobStore _blobs;
    private readonly ILogger _logger;

    internal DevWorkflowObjectiveComposer(IDevWorkflowArtifactBlobStore blobs, ILogger logger)
    {
        _blobs = blobs;
        _logger = logger;
    }

    private readonly record struct InputEntry(string Name, string Value);

    /// <summary>
    ///     What the agent is asked to do: the node's own instructions, the operator's request, and the artifacts the
    ///     nodes before it produced, recorded as consumed in the same breath.
    /// </summary>
    /// <remarks>
    ///     The audit then says what this attempt was given rather than what a later read can guess. Each upstream
    ///     artifact is rendered with its CONTENTS, not merely its name and id: a reference alone is useless to a node
    ///     with no way to dereference it — the seeded plan node told to turn research.md into a plan, handed only
    ///     "Report 'research.md' (version 1, id …)", would invent one. The bytes travel in the objective because that
    ///     is the one channel the agent lane already has.
    /// </remarks>
    internal async Task<string> ComposeAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        var objective = new StringBuilder();
        _ = objective.AppendLine(node.Instructions is { Length: > 0 } instructions ? instructions : $"Carry out the '{node.Label}' step of this development workflow.");

        // A node whose template subtree carries a DevTask writes work for the implementation lane, so it is told what that lane can and cannot do — in code, since that is the lane's
        // contract — gated on the SAME predicate the materializer refuses a task package by. Uncapped, landing before the policy phase reads objective.Length, so policy gets the rest.
        if (node.Materialization is { } materialization && graph.TemplateSubtreeHasDevTask(materialization))
        {
            _ = objective.AppendLine().AppendLine(DevWorkflowDecompositionContract.Text);
        }

        // The scoped rule sets sit between the node's instructions and what was asked, counted against the same budget so policy cannot crowd out the request it governs. They are read
        // from what the node run RECORDED — id, name, hash, text — never the rule set as it stands now, which an operator may edit mid-run into something the audit does not name.
        var policy = DevWorkflowRulePolicyResolver.Read(nodeRun.PolicyResolutionJson);
        var inputSection = InputSection(nodeRun, node, requestCap: int.MaxValue, out var request);

        // The request may be longer than the whole ceiling, and every Agent node is handed it: uncut it would drop every rule set, then overrun the work-session
        // limit. It gets the room the rest leaves, only HALF of it while a rule set applies; policy keeps the remainder, so a short request hands its slack on.
        if (request is not null)
        {
            var room = MaxObjectiveCharacters - objective.Length - (inputSection.Length - request.Length);
            var requestCap = Math.Max(0, policy.Count > 0 ? room / 2 : room);
            if (request.Length > requestCap)
            {
                inputSection = InputSection(nodeRun, node, requestCap, out _);
            }
        }

        // The fair share, the visible truncation marker and the dropped-with-a-warning are DevWorkflowPolicyText's, shared with the DevTask lane so the two cannot drift into rendering
        // the same recorded rule sets differently. The ceiling subtracts the input section, which is appended after this one and bounded only by the request's cut above.
        var policyCeiling = MaxObjectiveCharacters - inputSection.Length;
        _ = objective.Append(DevWorkflowPolicyText.Render(policy,
            policyCeiling,
            objective.Length,
            nodeRun.Id,
            _logger));

        _ = objective.Append(inputSection);

        var upstream = await DevWorkflowUpstreamArtifacts.RecordAsync(store, graph, run, nodeRun, cancellationToken);

        // What did NOT arrive belongs in the same section as what did. An All join carries on past a leaf a person skipped, so this node can be handed four implementations where the
        // fan-out was five wide, and with nothing saying so it would judge the four as the whole job. Named here rather than left to the absence.
        var skipped = await DevWorkflowUpstreamArtifacts.SkippedAsync(store, graph, run.Id, nodeRun.NodeKey, cancellationToken);
        var section = $"{Environment.NewLine}## What the steps before you produced{Environment.NewLine}";
        if ((upstream.Count > 0 || skipped.Count > 0) && objective.Length + section.Length <= MaxObjectiveCharacters)
        {
            _ = objective.Append(section);

            // Composed FIRST though appended last. One line per skipped step carrying the reason the row kept — for an operator's decision, their own words — with the heading folded
            // into the first line, so a budget that runs out leaves no heading promising steps it could not name.
            var lines = skipped.Select(static skip => string.Create(CultureInfo.InvariantCulture,
                                   $"- '{skip.NodeKey}' was skipped{(skip.TerminalReason is { Length: > 0 } reason ? $": {reason}" : ".")}{Environment.NewLine}"))
                               .ToList();
            if (lines.Count > 0)
            {
                lines[0] = $"{Environment.NewLine}### Skipped steps{Environment.NewLine}{lines[0]}";
            }

            // Their room is held BACK from what the artifacts apportion: the share below hands the bodies every remaining character, so one long document would truncate to the ceiling
            // and leave the lines nothing to fit into. Capped at half the room so the reserve cannot invert the problem, a wide fan-out of skips crowding out the branches that produced.
            var reserve = Math.Min(lines.Sum(static line => line.Length), (MaxObjectiveCharacters - objective.Length) / 2);
            var artifactCeiling = MaxObjectiveCharacters - reserve;
            for (var index = 0; index < upstream.Count; index++)
            {
                var artifact = upstream[index];
                var header = string.Create(CultureInfo.InvariantCulture,
                    $"{Environment.NewLine}### {artifact.Kind} '{artifact.Name}' (version {artifact.Version}, id {artifact.Id}){Environment.NewLine}");

                // Everything still to be written shares the room left equally — the room BELOW the reserve, not below the limit — so a long first document cannot crowd out the ones
                // after it and a short one hands its slack on. The share covers this artifact's own header and marker as well as its body, so both come off before the body is asked for.
                var share = (artifactCeiling - objective.Length) / (upstream.Count - index);
                var readMore = ArtifactMediaTypes.IsText(artifact.MediaType) ? ReadMoreMarker(artifact.Name) : null;
                var body = await RenderArtifactAsync(run.Id,
                    artifact,
                    share - header.Length - DevWorkflowPolicyText.TruncationMarkerReserve - (readMore?.Length ?? 0),
                    readMore,
                    cancellationToken);

                // The bound is enforced HERE, on the FINISHED block, counting header, body, whichever marker was rendered and the newlines. Nothing reaches the objective except through
                // this check. A block that will not fit falls back to its header and the read-more line, then its header alone, and one with no room even for that is dropped.
                foreach (var candidate in new[]
                         {
                             header + body + Environment.NewLine,
                             readMore is null ? header : $"{header}({readMore}){Environment.NewLine}",
                             header
                         })
                {
                    if (objective.Length + candidate.Length <= artifactCeiling)
                    {
                        _ = objective.Append(candidate);
                        break;
                    }
                }
            }

            // Last, and under the SAME overall bound as everything else rather than under the reserve: whatever the artifacts left unspent is the list's to use, and a line about work
            // that did not happen still must not push the objective past the limit.
            foreach (var line in lines)
            {
                if (objective.Length + line.Length > MaxObjectiveCharacters)
                {
                    break;
                }

                _ = objective.Append(line);
            }
        }

        return objective.ToString().TrimEnd();
    }

    /// <summary>One upstream artifact's contents, or the line that says why they are not here.</summary>
    /// <remarks>
    ///     Text only, decided from the DECLARED media type rather than by sniffing bytes, and only once the blob store
    ///     has verified the artifact row's own digest and size. An artifact whose bytes no longer match what produced
    ///     them is handed over as a reference and a warning: silently injecting unverified content is how a tampered
    ///     file would end up being reasoned about as if it were the research.
    /// </remarks>
    private async Task<string> RenderArtifactAsync(Guid runId, DevWorkflowArtifactSnapshot artifact, int budget, string? readMore, CancellationToken cancellationToken)
    {
        if (!ArtifactMediaTypes.IsText(artifact.MediaType))
        {
            return $"(Its bytes are {artifact.MediaType}, not text, so only this reference is given.)";
        }

        if (budget <= 0)
        {
            return $"(The objective had no room left for its contents, so only this reference is given.{(readMore is null ? "" : $" {readMore}")})";
        }

        if (artifact.SizeBytes > MaxInjectableArtifactBytes)
        {
            // Gated on the row's recorded size BEFORE the read, because reading is what costs: the blob store materializes the whole blob and the decode allocates a UTF-16 copy, so a
            // fan-in would spend hundreds of megabytes to keep a few thousand characters. A huge document loses even its prefix, which is right — and the marker says so.
            return $"(It is {artifact.SizeBytes} bytes, too large to include here, so only this reference is given.{(readMore is null ? "" : $" {readMore}")})";
        }

        var read = await _blobs.ReadAsync(runId, artifact.Id, artifact.ContentSha256, artifact.SizeBytes, cancellationToken);
        if (read.Status != DevWorkflowArtifactReadStatus.Found)
        {
            _logger.LogWarning("Development workflow artifact {ArtifactId} of run {RunId} was not injected into an objective: {Status}.",
                artifact.Id,
                runId,
                read.Status);
            return $"(Its stored bytes did not verify ({read.Status}), so only this reference is given. Do not assume what it said.)";
        }

        var content = Encoding.UTF8.GetString(read.Content.Span);
        if (content.Length <= budget)
        {
            return content;
        }

        var cut = DevWorkflowPolicyText.CutAt(content, budget);
        return $"{content[..cut]}{Environment.NewLine}(Truncated: the first {cut} of {content.Length} characters.{(readMore is null ? "" : $" {readMore}")})";
    }

    /// <summary>The sentence that tells the agent how to read past what the objective could inline, naming the artifact as the tool takes it.</summary>
    internal static string ReadMoreMarker(string name) =>
        $"Read the rest with {WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName} name=\"{name}\".";

    /// <summary>What was asked, the operator's retry reason and the output contract, the request cut to <paramref name="requestCap" />.</summary>
    /// <param name="request">The request uncut, or null when this node run carries none.</param>
    private static StringBuilder InputSection(DevWorkflowNodeRunSnapshot nodeRun, DevWorkflowGraphNode node, int requestCap, out string? request)
    {
        var inputSection = new StringBuilder();
        request = null;

        // The operator's retry reason is lifted OUT of the generic member list into its own sentence: a person who retried this step and said why is not one more anonymous input line, and
        // read as one it is what a model skims past. Only for the attempt their decision started — the reader is scoped by attempt, so a later automatic re-attempt carries no complaint.
        var input = ReadInput(nodeRun.InputJson)
                    .Where(static entry => entry.Name is not (DevWorkflowNodeInputs.OperatorRetryReason or DevWorkflowNodeInputs.OperatorRetryAttempt))
                    .ToList();
        if (input.Count > 0)
        {
            _ = inputSection.AppendLine().AppendLine("## What was asked");
            foreach (var (name, value) in input)
            {
                var rendered = value;
                if (name == DevWorkflowNodeInputs.WorkItemRequest)
                {
                    request = value;
                    rendered = CutRequest(value, requestCap);
                }

                _ = inputSection.AppendLine(CultureInfo.InvariantCulture, $"- {name}: {rendered}");
            }
        }

        if (DevWorkflowNodeInputs.OperatorRetryReasonFor(nodeRun.InputJson, nodeRun.Attempt) is { } operatorRetry)
        {
            _ = inputSection.AppendLine()
                            .AppendLine("## Operator retry")
                            .AppendLine("The operator retried this step and said:")
                            .AppendLine(operatorRetry);
        }

        // The output contract is the request's other half — what the session must hand back — so it is uncapped and counted with it.
        if (node.OutputSchema is { } outputSchema)
        {
            _ = inputSection.AppendLine().AppendLine(outputSchema.Render());
        }

        return inputSection;
    }

    /// <summary>The request whole when it fits, else as much as fits with the marker inside the cap; a cap under the marker gets only its start.</summary>
    internal static string CutRequest(string request, int cap)
    {
        if (request.Length <= cap)
        {
            return request;
        }

        var marker = RequestCutMarker(request.Length);
        var keep = cap - marker.Length;
        if (keep > 0)
        {
            return DevWorkflowStateMachine.Bounded(request, keep) + marker;
        }

        return cap > 0 ? DevWorkflowStateMachine.Bounded(marker.TrimStart(), cap) : string.Empty;
    }

    /// <summary>The line a cut request ends on.</summary>
    internal static string RequestCutMarker(int length) =>
        string.Create(CultureInfo.InvariantCulture, $" [request cut here to fit this step's objective; the whole request is {length} characters]");

    /// <summary>
    ///     The node run's input document as flat name/value lines. Nested and array values are rendered as their raw
    ///     JSON: a caller-supplied <c>inputsJson</c> is arbitrary, and reformatting it would be inventing structure it
    ///     does not have.
    /// </summary>
    private static IReadOnlyList<InputEntry> ReadInput(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return
            [
                .. document.RootElement.EnumerateObject()
                           .Where(static property => property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                           .Select(static property => new InputEntry(property.Name,
                               property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText()))
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
