namespace XE_Local_AI_Engine.Tests.Testing;

using System.ClientModel;
using System.ClientModel.Primitives;

/// <summary>Builds a real <see cref="ClientResultException" /> over a fake response, the shape the OpenAI SDK throws for a status failure.</summary>
internal static class FakeClientResultException
{
    // The default body carries only a field the failure classifier never reads, so its fixed message texts stay exact.
    public static ClientResultException Create(int status, string? retryAfter, string reasonPhrase = "",
        string body = """{"error":{"type":"body-sentinel"}}""")
    {
        // The fake owns nothing (its Dispose is empty), so the exception may keep reading it after this scope.
        using var response = new FakePipelineResponse(status, BinaryData.FromString(body), retryAfter, reasonPhrase);
        return new ClientResultException(response, innerException: null);
    }

    // A PipelineResponse with a fixed status, reason phrase, JSON body and optional Retry-After header, so GetRawResponse() is the real read path.
    private sealed class FakePipelineResponse : PipelineResponse
    {
        private readonly FakeHeaders _headers;

        public FakePipelineResponse(int status, BinaryData content, string? retryAfter, string reasonPhrase)
        {
            Status = status;
            Content = content;
            ReasonPhrase = reasonPhrase;
            _headers = new FakeHeaders(retryAfter);
        }

        public override int Status { get; }

        public override string ReasonPhrase { get; }

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content { get; }

        protected override PipelineResponseHeaders HeadersCore => _headers;

        public override BinaryData BufferContent(CancellationToken cancellationToken = default)
        {
            return Content;
        }

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(Content);
        }

        public override void Dispose()
        {
        }
    }

    private sealed class FakeHeaders : PipelineResponseHeaders
    {
        private readonly List<KeyValuePair<string, string>> _headers;

        public FakeHeaders(string? retryAfter)
        {
            _headers = retryAfter is null ? [] : [new KeyValuePair<string, string>("Retry-After", retryAfter)];
        }

        public override bool TryGetValue(string name, out string? value)
        {
            value = _headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            return value is not null;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            var found = _headers.Where(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Select(static header => header.Value).ToList();
            values = found.Count == 0 ? null : found;
            return found.Count > 0;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            return _headers.GetEnumerator();
        }
    }
}
