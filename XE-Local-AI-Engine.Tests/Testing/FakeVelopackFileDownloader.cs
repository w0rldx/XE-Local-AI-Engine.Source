namespace XE_Local_AI_Engine.Tests.Testing;

using System.Text;
using Velopack.Sources;

/// <summary>
///     Velopack's own <see cref="IFileDownloader" /> seam, backed by a URL → body map, so update-source tests run
///     with no network and no HTTP handler.
/// </summary>
/// <remarks>
///     An unmapped URL throws rather than returning an empty body: an unexpected request must fail loudly instead of
///     looking like an empty feed. The recorded request lists are what the per-check download bounds are asserted on.
/// </remarks>
public sealed class FakeVelopackFileDownloader : IFileDownloader
{
    private readonly List<string> _byteRequests = [];
    private readonly Dictionary<string, string> _responses;
    private readonly List<string> _stringRequests = [];

    public FakeVelopackFileDownloader(IReadOnlyDictionary<string, string> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        _responses = new Dictionary<string, string>(responses, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every URL passed to <see cref="DownloadString" />, in order. The releases-API pages.</summary>
    public IReadOnlyList<string> StringRequests => _stringRequests;

    /// <summary>Every URL passed to <see cref="DownloadBytes" />, in order. The per-release feed documents.</summary>
    public IReadOnlyList<string> ByteRequests => _byteRequests;

    /// <summary>Adds or replaces one canned response.</summary>
    public void Map(string url, string body)
    {
        _responses[url] = body;
    }

    public Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30.0, CancellationToken cancelToken = default)
    {
        throw new NotSupportedException("No test downloads a package; only the feed documents are read.");
    }

    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30.0)
    {
        _byteRequests.Add(url);
        return Task.FromResult(Encoding.UTF8.GetBytes(Body(url)));
    }

    public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30.0)
    {
        _stringRequests.Add(url);
        return Task.FromResult(Body(url));
    }

    private string Body(string url)
    {
        return _responses.TryGetValue(url, out var body)
            ? body
            : throw new InvalidOperationException($"Unexpected request for '{url}'.");
    }
}
