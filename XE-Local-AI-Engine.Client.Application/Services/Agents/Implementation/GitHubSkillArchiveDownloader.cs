namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Net;
using System.Text.RegularExpressions;

/// <summary>
///     Fetches a GitHub repository's default-branch archive for the import preview.
/// </summary>
/// <remarks>
///     <para>
///         The host is <em>never</em> caller-supplied: the API takes an owner and a repository name, not a URL. That is
///         the whole basis of the allowlist below — a pasted URL would let the caller choose the host and the allowlist
///         would stop meaning anything. Both slug parts are charset-restricted, and rejecting a leading dot is what
///         kills <c>..</c> path walking on the fixed host.
///     </para>
///     <para>
///         Redirects are followed manually (<c>AllowAutoRedirect = false</c> on the registered client, the same posture
///         the Hugging Face resolve client and the GitHub device-flow client use) because
///         <c>github.com → codeload.github.com</c> is a normal hop that has to be permitted while every other
///         destination is refused. Each hop is re-validated against the allowlist rather than only the first.
///     </para>
///     <para>
///         Two threats are deliberately <em>not</em> defended here: DNS rebinding, which buys nothing against an HTTPS
///         <em>hostname</em> allowlist (the attacker still needs a valid certificate for that name), and IP-literal
///         hosts, which are moot while the host is not caller-supplied. No TTL pinning is built.
///     </para>
/// </remarks>
internal sealed partial class GitHubSkillArchiveDownloader
{
    /// <summary>Named client registered with automatic redirects disabled — see the remarks above.</summary>
    internal const string HttpClientName = "skill-import-github";

    private const int MaxRedirectHops = 4;
    private const int MaxAttempts = 3;
    private const int ChunkSize = 64 * 1024;

    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> AllowedHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "github.com",
            "codeload.github.com"
        };

    private readonly HttpClient _httpClient;
    private readonly SkillImportOptions _options;

    public GitHubSkillArchiveDownloader(HttpClient httpClient, SkillImportOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Validates an <c>owner/repo</c> slug. Throws rather than sanitising: a malformed slug is a caller bug, not something to guess at.</summary>
    /// <exception cref="SkillImportException">Either part is empty or outside the permitted charset.</exception>
    public static void ValidateSlug(string? owner, string? repository)
    {
        if (!SlugPattern().IsMatch(owner ?? string.Empty) || !SlugPattern().IsMatch(repository ?? string.Empty))
        {
            throw new SkillImportException("The repository must be given as an owner and a repository name using only letters, digits, '.', '_' and '-', and neither may start with a dot.");
        }
    }

    /// <summary>Downloads the default-branch <c>.zip</c>, retrying transient failures with backoff.</summary>
    /// <exception cref="SkillImportException">The slug is malformed, a guard tripped, or the download failed for good.</exception>
    public async Task<byte[]> DownloadAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        ValidateSlug(owner, repository);

        // HEAD.zip is the default-branch archive; github.com answers it with a 302 to codeload.github.com.
        var uri = new Uri($"https://github.com/{owner}/{repository}/archive/HEAD.zip");

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await FetchAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < MaxAttempts && IsTransient(exception, cancellationToken))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1))), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                throw new SkillImportException("The repository download failed after several attempts. Check the connection and try again.", exception);
            }
        }

        // Unreachable: the final attempt either returns or throws through one of the filters above.
        throw new SkillImportException("The repository download failed after several attempts. Check the connection and try again.");
    }

    private async Task<byte[]> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= MaxRedirectHops; hop++)
        {
            EnsureAllowedHost(uri);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                uri = ResolveRedirect(uri, response.Headers.Location);
                continue;
            }

            EnsureSuccess(response.StatusCode);
            return await ReadCappedAsync(response.Content, _options.MaxArchiveBytes, cancellationToken).ConfigureAwait(false);
        }

        throw new SkillImportException("The repository download followed too many redirects and was abandoned.");
    }

    private static void EnsureAllowedHost(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !AllowedHosts.Contains(uri.Host))
        {
            throw new SkillImportException("The repository download targeted a host outside the GitHub allowlist and was refused.");
        }
    }

    private static Uri ResolveRedirect(Uri current, Uri? location)
    {
        if (location is null)
        {
            throw new SkillImportException("The repository download was redirected without a destination and was abandoned.");
        }

        // Relative locations resolve against the hop we came from; the result is re-validated at the top of the loop.
        return location.IsAbsoluteUri ? location : new Uri(current, location);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static void EnsureSuccess(HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.NotFound)
        {
            throw new SkillImportException("The repository was not found, or it has no downloadable default-branch archive.");
        }

        // Rate limiting and server faults are worth another attempt; anything else is a settled refusal.
        if (statusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)statusCode >= 500)
        {
            throw new HttpRequestException($"The repository download was rejected with status {(int)statusCode}.");
        }

        if ((int)statusCode >= 400)
        {
            throw new SkillImportException($"The repository download was rejected with status {(int)statusCode}.");
        }
    }

    /// <summary>
    ///     Streams the body under a hard byte cap and a read-idle deadline. The cap is applied to the bytes received —
    ///     the archive as it will be handed to <see cref="SkillArchiveReader" />, which caps the inflated size
    ///     separately — so neither a slow-drip stall nor an endless body can exhaust the node.
    /// </summary>
    private static async Task<byte[]> ReadCappedAsync(HttpContent content, int maxArchiveBytes, CancellationToken cancellationToken)
    {
        var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();

            // One linked CTS re-armed per read (CancelAfter reschedules its timer): free on the happy path, and a
            // genuine stall surfaces as a transient failure the retry loop above re-attempts.
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var chunk = new byte[ChunkSize];

            while (buffer.Length <= maxArchiveBytes)
            {
                idleCts.CancelAfter(ReadIdleTimeout);

                int read;
                try
                {
                    read = await source.ReadAsync(chunk, idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new HttpRequestException("The repository download stalled with no data received.");
                }

                if (read == 0)
                {
                    return buffer.ToArray();
                }

                await buffer.WriteAsync(chunk.AsMemory(start: 0, read), cancellationToken).ConfigureAwait(false);
            }

            throw new SkillImportException($"The repository archive is larger than the {maxArchiveBytes / (1024 * 1024)} MiB import limit.");
        }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        return exception switch
        {
            SkillImportException => false,
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            HttpRequestException or IOException => true,
            _ => false
        };
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SlugPattern();
}
