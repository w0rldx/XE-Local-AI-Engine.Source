namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Marker H <see cref="IAgentHomeMemoryProposalService" />. Reads the agent-written JSONL files from
///     <c>runs/&lt;run-id&gt;/memory/proposals/</c>, validates each line against the §10 MVP schema, applies the
///     <see cref="MemoryProposalSecretScanner" /> per record, and returns surviving proposals together with a rejection
///     log. Never mutates real node/platform memory — caller is responsible for later user/platform review.
/// </summary>
internal sealed class AgentHomeMemoryProposalService : IAgentHomeMemoryProposalService
{
    private static readonly JsonDocumentOptions JsonDocOptions = new() { AllowTrailingCommas = false };

    // Valid closed-enum values (§10 MVP schema).
    private static readonly HashSet<string> ValidTypes = new(StringComparer.Ordinal)
    {
        "node_memory_proposal",
        "project_memory_proposal"
    };

    private static readonly HashSet<string> ValidOperations = new(StringComparer.Ordinal)
    {
        "add",
        "update",
        "remove"
    };

    private static readonly HashSet<string> ValidConfidences = new(StringComparer.Ordinal)
    {
        "low",
        "medium",
        "high"
    };

    private const int MaxContentLength = 4000;
    private const int MinContentLength = 1;

    private readonly ILogger<AgentHomeMemoryProposalService> _logger;

    public AgentHomeMemoryProposalService(ILogger<AgentHomeMemoryProposalService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MemoryProposalCollectResult> CollectAsync(
        MemoryProposalCollectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var proposalsDirectory = Path.Combine(request.HostRunDirectory, "memory", "proposals");
        if (!Directory.Exists(proposalsDirectory))
        {
            // No proposals directory means the agent wrote nothing — not an error.
            _logger.LogDebug(
                "No memory/proposals directory found for run {RunId}; returning empty result.",
                request.RunId);
            return EmptyResult();
        }

        var proposals = new List<MemoryProposalRecord>();
        var rejections = new List<MemoryProposalRejection>();

        // Only the two canonical file names are collected (§10); other files in the directory are ignored.
        var candidateFiles = new[]
        {
            "node-memory.proposals.jsonl",
            "project-memory.proposals.jsonl"
        };

        foreach (var fileName in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = Path.Combine(proposalsDirectory, fileName);
            if (!File.Exists(filePath))
            {
                continue;
            }

            await ReadJsonlFileAsync(filePath, fileName, proposals, rejections, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Memory proposal collection for run {RunId}: {ProposalCount} accepted, {RejectionCount} rejected.",
            request.RunId,
            proposals.Count,
            rejections.Count);

        return new MemoryProposalCollectResult
        {
            Proposals = proposals,
            Rejections = rejections
        };
    }

    private async Task ReadJsonlFileAsync(
        string filePath,
        string fileName,
        List<MemoryProposalRecord> proposals,
        List<MemoryProposalRejection> rejections,
        CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read proposal file {FileName}; skipping.", fileName);
            return;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                // Blank lines in JSONL are valid separators; skip silently.
                continue;
            }

            var rejection = TryParseLine(line, fileName, i, out var record);
            if (rejection is not null)
            {
                _logger.LogDebug(
                    "Proposal line {LineIndex} in {FileName} rejected: {Reason}",
                    i,
                    fileName,
                    rejection.Reason);
                rejections.Add(rejection);
            }
            else if (record is not null)
            {
                proposals.Add(record);
            }
        }
    }

    /// <summary>
    ///     Parses and validates one JSONL line. Returns a <see cref="MemoryProposalRejection" /> when the record must be
    ///     rejected, or <see langword="null" /> on success (with <paramref name="record" /> set).
    /// </summary>
    private static MemoryProposalRejection? TryParseLine(
        string line,
        string fileName,
        int lineIndex,
        out MemoryProposalRecord? record)
    {
        record = null;

        // ── 1. Parse JSON ────────────────────────────────────────────────────
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line, nodeOptions: null, documentOptions: JsonDocOptions);
        }
        catch (JsonException)
        {
            return Reject(fileName, lineIndex, "line is not valid JSON");
        }

        if (node is not JsonObject obj)
        {
            return Reject(fileName, lineIndex, "line is not a JSON object");
        }

        // ── 2. Extract required fields ───────────────────────────────────────
        if (!TryGetString(obj, "type", out var type))
        {
            return Reject(fileName, lineIndex, "missing or non-string 'type' field");
        }

        if (!TryGetString(obj, "operation", out var operation))
        {
            return Reject(fileName, lineIndex, "missing or non-string 'operation' field");
        }

        if (!TryGetString(obj, "content", out var content))
        {
            return Reject(fileName, lineIndex, "missing or non-string 'content' field");
        }

        if (!TryGetString(obj, "confidence", out var confidence))
        {
            return Reject(fileName, lineIndex, "missing or non-string 'confidence' field");
        }

        // evidence is required but may be an empty array (§10).
        if (!TryGetStringArray(obj, "evidence", out var evidence))
        {
            return Reject(fileName, lineIndex, "missing or invalid 'evidence' field (must be a string array)");
        }

        // ── 3. Closed-enum validation ────────────────────────────────────────
        if (!ValidTypes.Contains(type))
        {
            return Reject(fileName, lineIndex, $"unknown 'type' value '{type}'; expected node_memory_proposal or project_memory_proposal");
        }

        if (!ValidOperations.Contains(operation))
        {
            return Reject(fileName, lineIndex, $"unknown 'operation' value '{operation}'; expected add, update, or remove");
        }

        if (!ValidConfidences.Contains(confidence))
        {
            return Reject(fileName, lineIndex, $"unknown 'confidence' value '{confidence}'; expected low, medium, or high");
        }

        // ── 4. Content length ────────────────────────────────────────────────
        if (content.Length < MinContentLength || content.Length > MaxContentLength)
        {
            return Reject(fileName, lineIndex, $"'content' length {content.Length} is outside the allowed range [{MinContentLength}, {MaxContentLength}]");
        }

        // ── 5. Evidence path prefix validation (§10/§11: must reference sandbox paths, never host paths) ──
        if (evidence.Any(path => path.Contains("..", StringComparison.Ordinal)))
        {
            return Reject(fileName, lineIndex, "evidence path contains a path-traversal segment '..'");
        }

        // Reject absolute/rooted HOST paths (Path.IsPathRooted, leading '/' or '\\', or an 'X:' drive). The only
        // allowed rooted form is the in-sandbox workspace root; any other absolute path is a worker-host path that must
        // not land in MemoryProposalRecord.Evidence (§11). Relative paths are allowed.
        if (evidence.Any(IsDisallowedEvidencePath))
        {
            return Reject(fileName, lineIndex, "evidence path is an absolute host path; only sandbox-relative or workspace-rooted paths are allowed");
        }

        // ── 6. Secret scan ───────────────────────────────────────────────────
        var scanResult = MemoryProposalSecretScanner.Scan(type, operation, content, evidence, confidence);
        if (scanResult.ShouldReject)
        {
            return Reject(fileName, lineIndex, scanResult.RejectionReason ?? "secret detected in proposal record");
        }

        var finalContent = scanResult.RedactedContent ?? content;

        record = new MemoryProposalRecord
        {
            Type = type,
            Operation = operation,
            Content = finalContent,
            Evidence = evidence,
            Confidence = confidence,
            SourceLineIndex = lineIndex,
            SourceFileName = fileName
        };

        return null;
    }

    /// <summary>
    ///     <see langword="true" /> when an evidence path is an absolute/rooted HOST path that must not be persisted
    ///     (§11). A path is disallowed when it is rooted (<see cref="Path.IsPathRooted(string)" />), starts with a
    ///     directory separator, or carries a Windows drive prefix — UNLESS it is under the in-sandbox workspace root,
    ///     which is the only legitimate rooted form for evidence. Relative paths are always allowed.
    /// </summary>
    private static bool IsDisallowedEvidencePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var isRooted = Path.IsPathRooted(path)
                       || path[0] is '/' or '\\'
                       || HasWindowsDrivePrefix(path);
        if (!isRooted)
        {
            return false;
        }

        // The sandbox workspace root is the only allowed rooted prefix; anything else rooted is a host path.
        return !path.StartsWith(AgentHomeGit.WorkspaceSelectedRoot + "/", StringComparison.Ordinal)
               && !string.Equals(path, AgentHomeGit.WorkspaceSelectedRoot, StringComparison.Ordinal);
    }

    private static bool HasWindowsDrivePrefix(string path)
    {
        return path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
    }

    // ── JSON helpers ──────────────────────────────────────────────────────

    private static bool TryGetString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return false;
        }

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? str) || str is null)
        {
            return false;
        }

        value = str;
        return true;
    }

    private static bool TryGetStringArray(JsonObject obj, string key, out IReadOnlyList<string> value)
    {
        value = [];
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return false;
        }

        if (node is not JsonArray arr)
        {
            return false;
        }

        var result = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonValue jv || !jv.TryGetValue(out string? str) || str is null)
            {
                return false;
            }

            result.Add(str);
        }

        value = result;
        return true;
    }

    private static MemoryProposalRejection Reject(string fileName, int lineIndex, string reason)
    {
        return new MemoryProposalRejection
        {
            SourceFileName = fileName,
            SourceLineIndex = lineIndex,
            Reason = reason
        };
    }

    private static MemoryProposalCollectResult EmptyResult()
    {
        return new MemoryProposalCollectResult
        {
            Proposals = [],
            Rejections = []
        };
    }
}
