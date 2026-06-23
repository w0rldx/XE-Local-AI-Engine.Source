namespace XE_Local_AI_Engine.Client.Services.Coder.Tools;

/// <summary>
///     Typed projection of the <c>list_files</c> JSON arguments. The bridge stays JSON-in / JSON-out, so the handler
///     deserializes into this record and validates it before any sandbox call.
/// </summary>
internal sealed record ListFilesToolRequest
{
    public string? Path { get; init; }

    public string? Glob { get; init; }

    public int? MaxResults { get; init; }
}

/// <summary>Typed projection of the <c>read_file</c> JSON arguments.</summary>
internal sealed record ReadFileToolRequest
{
    public string? Path { get; init; }

    public int? StartLine { get; init; }

    public int? EndLine { get; init; }
}

/// <summary>Typed projection of the <c>search_text</c> JSON arguments.</summary>
internal sealed record SearchTextToolRequest
{
    public string? Pattern { get; init; }

    public string? Path { get; init; }

    public bool? IsRegex { get; init; }

    public int? MaxMatches { get; init; }
}

/// <summary>
///     Validates the three coder tool requests against their constraints before any sandbox call (reject-before-side-
///     effect, like <c>AgentHomeRunToolRequestValidator</c>). The advertised JSON schema is advisory; this validation
///     is authoritative. Path confinement itself is the reader's job (<see cref="WorkspacePathGuard" />); these
///     validators only enforce required/shape constraints the schema declares.
/// </summary>
internal static class CoderToolRequestValidator
{
    private const int MaxPathLength = 4096;
    private const int MaxGlobLength = 512;
    private const int MaxPatternLength = 1024;

    public static IReadOnlyList<string> Validate(ListFilesToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();

        if (request.Path is { Length: > MaxPathLength })
        {
            errors.Add($"'path' must be at most {MaxPathLength} characters.");
        }

        if (request.Glob is { Length: > MaxGlobLength })
        {
            errors.Add($"'glob' must be at most {MaxGlobLength} characters.");
        }

        if (request.MaxResults is { } maxResults && maxResults < 1)
        {
            errors.Add("'maxResults' must be a positive integer.");
        }

        return errors;
    }

    public static IReadOnlyList<string> Validate(ReadFileToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            errors.Add("'path' is required and must be a non-empty string.");
        }
        else if (request.Path.Length > MaxPathLength)
        {
            errors.Add($"'path' must be at most {MaxPathLength} characters.");
        }

        if (request.StartLine is { } startLine && startLine < 1)
        {
            errors.Add("'startLine' must be a positive integer.");
        }

        if (request.EndLine is { } endLine && endLine < 1)
        {
            errors.Add("'endLine' must be a positive integer.");
        }

        if (request.StartLine is { } start && request.EndLine is { } end && end < start)
        {
            errors.Add("'endLine' must be greater than or equal to 'startLine'.");
        }

        return errors;
    }

    public static IReadOnlyList<string> Validate(SearchTextToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Pattern))
        {
            errors.Add("'pattern' is required and must be a non-empty string.");
        }
        else if (request.Pattern.Length > MaxPatternLength)
        {
            errors.Add($"'pattern' must be at most {MaxPatternLength} characters.");
        }

        if (request.Path is { Length: > MaxPathLength })
        {
            errors.Add($"'path' must be at most {MaxPathLength} characters.");
        }

        if (request.MaxMatches is { } maxMatches && maxMatches < 1)
        {
            errors.Add("'maxMatches' must be a positive integer.");
        }

        // A regex pattern that does not compile is rejected before any sandbox call so a bad expression never reaches
        // the grep invocation.
        if (request.IsRegex == true && !string.IsNullOrWhiteSpace(request.Pattern))
        {
            try
            {
                _ = System.Text.RegularExpressions.Regex.Match(string.Empty,
                    request.Pattern,
                    System.Text.RegularExpressions.RegexOptions.None,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                errors.Add("'pattern' is not a valid regular expression.");
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                errors.Add("'pattern' is not a valid regular expression.");
            }
        }

        return errors;
    }
}
