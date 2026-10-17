namespace XE_Local_AI_Engine.Cli.Api;

using System.Globalization;
using System.Net;
using System.Text.Json;

/// <summary>A non-success answer from the node, read from any of its three error bodies (wiki 09, section 4).</summary>
public sealed class NodeApiException : Exception
{
    private const int MaxErrorBodyBytes = 64 * 1024;

    public NodeApiException(HttpStatusCode statusCode, string message, string? conflictType = null, IReadOnlyList<string>? errors = null)
        : base(message)
    {
        StatusCode = statusCode;
        ConflictType = conflictType;
        Errors = errors ?? [];
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>The <c>conflictType</c> discriminator of a 409 <c>ConflictProblemDetails</c>, or null.</summary>
    public string? ConflictType { get; }

    /// <summary>Per-field reasons from a FastEndpoints or ASP.NET validation body.</summary>
    public IReadOnlyList<string> Errors { get; }

    internal static async Task<NodeApiException> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = response.StatusCode;
        var fallback = string.Create(CultureInfo.InvariantCulture, $"The node answered {(int)status} {response.ReasonPhrase}.");
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (body.Length is 0 or > MaxErrorBodyBytes)
        {
            return new NodeApiException(status, fallback);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new NodeApiException(status, fallback);
            }

            var errors = ReadErrors(root);
            var message = ReadString(root, "detail") ?? ReadString(root, "message") ?? errors.FirstOrDefault() ?? ReadString(root, "title") ?? fallback;
            return new NodeApiException(status, message, ReadString(root, "conflictType"), errors);
        }
        catch (JsonException)
        {
            return new NodeApiException(status, fallback);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    // FastEndpoints writes errors as [{name, reason}]; ASP.NET validation problems write {field: [messages]}.
    private static List<string> ReadErrors(JsonElement root)
    {
        var errors = new List<string>();
        if (!root.TryGetProperty("errors", out var value))
        {
            return errors;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            errors.AddRange(value.EnumerateArray()
                                 .Where(static item => item.ValueKind == JsonValueKind.Object)
                                 .Select(static item => ReadString(item, "reason"))
                                 .OfType<string>());
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            errors.AddRange(value.EnumerateObject()
                                 .Where(static field => field.Value.ValueKind == JsonValueKind.Array)
                                 .SelectMany(static field => field.Value.EnumerateArray())
                                 .Where(static item => item.ValueKind == JsonValueKind.String)
                                 .Select(static item => item.GetString())
                                 .OfType<string>());
        }

        return errors;
    }
}
