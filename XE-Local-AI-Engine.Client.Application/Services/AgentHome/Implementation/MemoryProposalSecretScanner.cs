namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Text.RegularExpressions;

/// <summary>
///     Regex-based secret scanner for memory proposal records. Applies per-match dispositions:
///     <list type="bullet">
///         <item>
///             <description>
///                 PEM/private-key blocks and Google service-account JSON → reject the whole record (the secret cannot
///                 be safely redacted because the structural context is the secret).
///             </description>
///         </item>
///         <item>
///             <description>
///                 Any secret found in <c>evidence</c> paths or the <c>type</c>/<c>operation</c>/<c>confidence</c>
///                 metadata fields → reject the whole record (metadata must not carry secrets).
///             </description>
///         </item>
///         <item>
///             <description>
///                 Secrets found in the <c>content</c> field only → redact as <c>[REDACTED:&lt;class&gt;]</c> and
///                 return the record (still useful to the reviewer).
///             </description>
///         </item>
///     </list>
///     This is not comprehensive DLP. The UI/API must label proposals as untrusted until reviewed.
/// </summary>
internal static partial class MemoryProposalSecretScanner
{
    // ── Reject-whole-record patterns ──────────────────────────────────────
    // PEM private-key blocks (any variant).
    // Match timeout (milliseconds) guarding every pattern below against pathological/ReDoS inputs.
    private const int RegexTimeoutMilliseconds = 2000;

    [GeneratedRegex(@"-----BEGIN\s+(?:[A-Z ]+\s+)?PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex PemPrivateKeyRegex();

    // OpenSSH private-key blocks.
    [GeneratedRegex(@"-----BEGIN OPENSSH PRIVATE KEY-----", RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex OpenSshPrivateKeyRegex();

    // Google service-account JSON: must have both "type": "service_account" AND private_key.
    [GeneratedRegex(@"""type""\s*:\s*""service_account""", RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex ServiceAccountTypeRegex();

    [GeneratedRegex(@"""private_key""", RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex ServiceAccountPrivateKeyRegex();

    // ── Redact-in-content patterns ────────────────────────────────────────
    // Assignment-like secrets: api_key=, secret:, password =, connectionstring=, client_secret=, access_token=, refresh_token=
    // followed by a non-whitespace, non-comment value (single/double quoted or bare word).
    [GeneratedRegex(@"(?:api[_\-]?key|secret|password|connectionstring|client_secret|access_token|refresh_token)\s*(?:=|:|:=|"":\s*""|':\s*')[^\s,;}{""'\r\n]{4,}",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex AssignmentSecretRegex();

    // GitHub tokens.
    [GeneratedRegex(@"gh[pos]_[A-Za-z0-9]{36,}", RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex GitHubTokenRegex();

    // GitHub fine-grained PATs.
    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]{82,}", RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex GitHubPatRegex();

    // AWS access key IDs.
    [GeneratedRegex(@"AKIA[0-9A-Z]{16}", RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex AwsAccessKeyRegex();

    // Azure storage connection strings.
    [GeneratedRegex(@"DefaultEndpointsProtocol=[^;]+;AccountName=[^;]+;AccountKey=[A-Za-z0-9+/=]{20,}", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture,
        RegexTimeoutMilliseconds)]
    private static partial Regex AzureConnectionStringRegex();

    // Azure AccountKey= standalone.
    [GeneratedRegex(@"AccountKey=[A-Za-z0-9+/=]{20,}", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex AzureAccountKeyRegex();

    // Slack tokens.
    [GeneratedRegex(@"xox[baprs]-[A-Za-z0-9\-]{10,}", RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex SlackTokenRegex();

    // JWT-looking values (three base64url segments).
    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex JwtRegex();

    // High-entropy bearer-like substrings (≥32 chars, token context keyword, Shannon entropy checked separately).
    [GeneratedRegex(@"(?:Bearer|sk-|token|key)\s+(?<token>[A-Za-z0-9+/=_\-]{32,})",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex BearerLikeRegex();

    // Keyword-free high-entropy fallback: a delimited base64/hex-ish run of ≥32 chars with no surrounding token keyword.
    // The whitespace/start/end boundaries (rather than \b) keep the '[REDACTED:…]' markers — which contain '[' and ']' —
    // out of the match, so a second pass never re-redacts an already-redacted span. Shannon entropy is gated separately
    // so an ordinary long identifier (low entropy) is left intact.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/=_\-])(?<token>[A-Za-z0-9+/=_\-]{32,})(?![A-Za-z0-9+/=_\-])",
        RegexOptions.Singleline | RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex BareHighEntropyTokenRegex();

    /// <summary>
    ///     Scans a validated proposal record. Returns a <see cref="ScanResult" /> indicating whether the record should
    ///     be rejected and/or its content redacted.
    /// </summary>
    internal static ScanResult Scan(string type,
        string operation,
        string content,
        IReadOnlyList<string> evidence,
        string confidence)
    {
        // ── 1. Whole-record reject: PEM private-key blocks ──────────────────
        if (PemPrivateKeyRegex().IsMatch(content)
            || OpenSshPrivateKeyRegex().IsMatch(content)
            || PemPrivateKeyRegex().IsMatch(type)
            || PemPrivateKeyRegex().IsMatch(operation))
        {
            return new ScanResult
            {
                ShouldReject = true,
                RejectionReason = "record contains a PEM private-key block"
            };
        }

        // ── 2. Whole-record reject: Google service-account JSON ─────────────
        if (ServiceAccountTypeRegex().IsMatch(content) && ServiceAccountPrivateKeyRegex().IsMatch(content))
        {
            return new ScanResult
            {
                ShouldReject = true,
                RejectionReason = "record contains a Google service-account JSON block"
            };
        }

        // ── 3. Whole-record reject: secrets in metadata fields ──────────────
        if (new[]
            {
                type,
                operation,
                confidence
            }.Any(ContainsAnyRedactPattern))
        {
            return new ScanResult
            {
                ShouldReject = true,
                RejectionReason = "secret detected in a metadata field (type/operation/confidence)"
            };
        }

        if (evidence.Any(path => ContainsAnyRedactPattern(path) || PemPrivateKeyRegex().IsMatch(path)))
        {
            return new ScanResult
            {
                ShouldReject = true,
                RejectionReason = "secret detected in an evidence path"
            };
        }

        // ── 4. Redact content matches ───────────────────────────────────────
        var redacted = RedactContent(content);
        var contentChanged = !string.Equals(redacted, content, StringComparison.Ordinal);

        return new ScanResult
        {
            RedactedContent = contentChanged ? redacted : null
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool ContainsAnyRedactPattern(string value)
    {
        return AssignmentSecretRegex().IsMatch(value)
               || GitHubTokenRegex().IsMatch(value)
               || GitHubPatRegex().IsMatch(value)
               || AwsAccessKeyRegex().IsMatch(value)
               || AzureConnectionStringRegex().IsMatch(value)
               || AzureAccountKeyRegex().IsMatch(value)
               || SlackTokenRegex().IsMatch(value)
               || JwtRegex().IsMatch(value)
               || HasHighEntropyBearerMatch(value)
               || HasBareHighEntropyToken(value);
    }

    private static string RedactContent(string content)
    {
        // Apply all redact patterns in a stable order; each pass replaces on the current string.
        content = AssignmentSecretRegex().Replace(content, "[REDACTED:assignment-secret]");
        content = GitHubTokenRegex().Replace(content, "[REDACTED:github-token]");
        content = GitHubPatRegex().Replace(content, "[REDACTED:github-pat]");
        content = AwsAccessKeyRegex().Replace(content, "[REDACTED:aws-access-key]");
        content = AzureConnectionStringRegex().Replace(content, "[REDACTED:azure-connection-string]");
        content = AzureAccountKeyRegex().Replace(content, "[REDACTED:azure-account-key]");
        content = SlackTokenRegex().Replace(content, "[REDACTED:slack-token]");
        content = JwtRegex().Replace(content, "[REDACTED:jwt]");
        content = RedactHighEntropyBearer(content);
        // Fallback last: catch a bare high-entropy token that no keyword/specific pattern matched. Runs after the
        // keyword passes so it only sees whatever survived, and its delimiters exclude the '[REDACTED:…]' markers.
        content = RedactBareHighEntropyTokens(content);
        return content;
    }

    private static bool HasHighEntropyBearerMatch(string value)
    {
        foreach (Match match in BearerLikeRegex().Matches(value))
        {
            var candidate = match.Groups["token"].Value;
            if (ShannonEntropy(candidate) >= 4.5)
            {
                return true;
            }
        }

        return false;
    }

    private static string RedactHighEntropyBearer(string content)
    {
        return BearerLikeRegex().Replace(content, match =>
        {
            var candidate = match.Groups["token"].Value;
            if (ShannonEntropy(candidate) >= 4.5)
            {
                // Keep the keyword; redact the high-entropy token only. The token group's Index is absolute (into the
                // whole content), so subtract match.Index to get the keyword length relative to match.Value — otherwise
                // a match at a non-zero offset slices past the keyword (leaking token bytes) or throws when the absolute
                // index exceeds match.Value.Length.
                var keywordLength = match.Groups["token"].Index - match.Index;
                return match.Value[..keywordLength] + "[REDACTED:high-entropy-token]";
            }

            return match.Value;
        });
    }

    private static bool HasBareHighEntropyToken(string value)
    {
        return BareHighEntropyTokenRegex().Matches(value)
                                          .Any(match => ShannonEntropy(match.Groups["token"].Value) >= 4.5);
    }

    private static string RedactBareHighEntropyTokens(string content)
    {
        return BareHighEntropyTokenRegex().Replace(content, match =>
        {
            var candidate = match.Groups["token"].Value;
            return ShannonEntropy(candidate) >= 4.5 ? "[REDACTED:high-entropy-token]" : match.Value;
        });
    }

    /// <summary>Shannon entropy (bits per character) of <paramref name="value" />.</summary>
    private static double ShannonEntropy(string value)
    {
        if (value.Length == 0)
        {
            return 0.0;
        }

        // Count character frequencies.
        var freq = new Dictionary<char, int>(value.Length);
        foreach (var ch in value)
        {
            freq.TryGetValue(ch, out var count);
            freq[ch] = count + 1;
        }

        var len = (double)value.Length;
        var entropy = 0.0;
        foreach (var count in freq.Values)
        {
            var p = count / len;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    ///     Outcome of scanning a single proposal record. <see cref="ShouldReject" /> is set when the record must be
    ///     rejected outright. <see cref="RedactedContent" /> is non-null when the content was modified but the record
    ///     is still usable.
    /// </summary>
    internal readonly struct ScanResult
    {
        public bool ShouldReject { get; init; }
        public string? RejectionReason { get; init; }
        public string? RedactedContent { get; init; }
    }
}
