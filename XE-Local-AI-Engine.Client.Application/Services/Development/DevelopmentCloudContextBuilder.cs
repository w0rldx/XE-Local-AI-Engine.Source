namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text.Json;

public sealed record DevelopmentCloudContextBuildRequest(
    string BundleId,
    string ProjectId,
    string TaskId,
    string AttemptId,
    string ProviderName,
    string ModelId,
    string Requirements,
    string AcceptanceCriteria,
    string PolicyText,
    IReadOnlyList<DevelopmentCloudContextExcerpt> Excerpts,
    DateTimeOffset ExpiresAt,
    string Nonce);

public interface IDevelopmentCloudContextBuilder
{
    DevelopmentCloudContextBundle Build(DevelopmentCloudContextBuildRequest request);
}

public sealed class DevelopmentCloudContextBuilder : IDevelopmentCloudContextBuilder
{
    public const int DefaultMaximumBytes = 64 * 1024;
    public const int DefaultMaximumEstimatedTokens = 16 * 1024;

    private readonly int _maximumBytes;
    private readonly int _maximumEstimatedTokens;
    private readonly TimeProvider _timeProvider;

    public DevelopmentCloudContextBuilder(TimeProvider timeProvider,
        int maximumBytes = DefaultMaximumBytes,
        int maximumEstimatedTokens = DefaultMaximumEstimatedTokens)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEstimatedTokens);

        _maximumBytes = maximumBytes;
        _maximumEstimatedTokens = maximumEstimatedTokens;
    }

    public DevelopmentCloudContextBundle Build(DevelopmentCloudContextBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequired(request);
        if (request.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException("The Development cloud context expiry must be in the future.");
        }

        var excerpts = request.Excerpts
                              .Select(static excerpt => SanitizeExcerpt(excerpt))
                              .OrderBy(static excerpt => excerpt.RelativePath, StringComparer.Ordinal)
                              .ToArray();
        if (excerpts.Select(static excerpt => excerpt.RelativePath).Distinct(StringComparer.Ordinal).Count() != excerpts.Length)
        {
            throw new InvalidOperationException("Development cloud context excerpt paths must be unique.");
        }

        var requirements = DevelopmentArtifactSanitizer.SanitizeText(request.Requirements);
        var acceptanceCriteria = DevelopmentArtifactSanitizer.SanitizeText(request.AcceptanceCriteria);
        var policyText = DevelopmentArtifactSanitizer.SanitizeText(request.PolicyText);
        var canonicalContent = SerializeCanonical(request, requirements, acceptanceCriteria, policyText, excerpts);
        var estimatedTokens = checked((canonicalContent.Length + 3) / 4);
        if (canonicalContent.Length > _maximumBytes)
        {
            throw new InvalidOperationException($"The Development cloud context exceeds the configured {_maximumBytes}-byte limit.");
        }
        if (estimatedTokens > _maximumEstimatedTokens)
        {
            throw new InvalidOperationException($"The Development cloud context exceeds the configured {_maximumEstimatedTokens}-token estimate.");
        }

        return new DevelopmentCloudContextBundle(request.BundleId,
            request.ProjectId,
            request.TaskId,
            request.AttemptId,
            request.ProviderName,
            request.ModelId,
            requirements,
            acceptanceCriteria,
            policyText,
            excerpts,
            Convert.ToHexString(SHA256.HashData(canonicalContent)),
            canonicalContent.Length,
            estimatedTokens,
            request.ExpiresAt,
            request.Nonce,
            secretScanPassed: true);
    }

    private static void ValidateRequired(DevelopmentCloudContextBuildRequest request)
    {
        var required = new[]
        {
            request.BundleId,
            request.ProjectId,
            request.TaskId,
            request.AttemptId,
            request.ProviderName,
            request.ModelId,
            request.Requirements,
            request.AcceptanceCriteria,
            request.PolicyText,
            request.Nonce
        };
        if (required.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Development cloud context identity, routing, content, and nonce values must not be blank.", nameof(request));
        }
        if (request.Excerpts is null)
        {
            throw new ArgumentException("Development cloud context excerpts must be supplied.", nameof(request));
        }
    }

    private static DevelopmentCloudContextExcerpt SanitizeExcerpt(DevelopmentCloudContextExcerpt excerpt)
    {
        ArgumentNullException.ThrowIfNull(excerpt);
        var relativePath = excerpt.RelativePath.Replace('\\', '/');
        var segments = relativePath.Split('/');
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath[0] == '/'
            || (relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':')
            || relativePath.Any(char.IsControl)
            || segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new DevelopmentWorkspaceSecurityException("Development cloud context excerpts require canonical repository-relative paths.");
        }

        return new DevelopmentCloudContextExcerpt(relativePath, DevelopmentArtifactSanitizer.SanitizeText(excerpt.Content));
    }

    private static byte[] SerializeCanonical(DevelopmentCloudContextBuildRequest request,
        string requirements,
        string acceptanceCriteria,
        string policyText,
        IReadOnlyList<DevelopmentCloudContextExcerpt> excerpts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("bundleId", request.BundleId);
            writer.WriteString("projectId", request.ProjectId);
            writer.WriteString("taskId", request.TaskId);
            writer.WriteString("attemptId", request.AttemptId);
            writer.WriteString("provider", request.ProviderName);
            writer.WriteString("model", request.ModelId);
            writer.WriteString("expiresAt", request.ExpiresAt);
            writer.WriteString("nonce", request.Nonce);
            writer.WriteString("requirements", requirements);
            writer.WriteString("acceptanceCriteria", acceptanceCriteria);
            writer.WriteString("policy", policyText);
            writer.WriteStartArray("excerpts");
            foreach (var excerpt in excerpts)
            {
                writer.WriteStartObject();
                writer.WriteString("path", excerpt.RelativePath);
                writer.WriteString("content", excerpt.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
