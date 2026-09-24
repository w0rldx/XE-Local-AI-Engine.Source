namespace XE_Local_AI_Engine.Desktop.Linux;

using System.Text.Json;

internal sealed class GtkSaveIntent
{
    public required string Nonce { get; init; }
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string Filename { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }

    internal const long MaximumBytes = 50 * 1024 * 1024;

    internal static GtkSaveIntent? Parse(string? body, Uri origin, string? nonce)
    {
        if (body is null || body.Length > 4096 || nonce is null) { return null; }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.GetProperty("kind").GetString() != "xe-save" || root.GetProperty("nonce").GetString() != nonce) { return null; }

            var id = root.GetProperty("id").GetString();
            var url = root.GetProperty("url").GetString();
            var filename = root.GetProperty("filename").GetString();
            var sha = root.GetProperty("sha256").GetString();
            var size = root.GetProperty("size").GetInt64();
            if (!Guid.TryParseExact(id, "D", out _) || url is null || !url.StartsWith("blob:", StringComparison.Ordinal)
                || !Uri.TryCreate(url[5..], UriKind.Absolute, out var inner) || !GtkDocumentPolicy.SameOrigin(origin, inner.AbsoluteUri)
                || !string.IsNullOrEmpty(inner.Query) || !string.IsNullOrEmpty(inner.Fragment)
                || !Guid.TryParseExact(inner.AbsolutePath[1..], "D", out _)
                || !ValidFilename(filename) || size is < 0 or > MaximumBytes || sha is null || sha.Length != 64
                || sha.Any(character => !char.IsAsciiHexDigit(character))) { return null; }

            return new GtkSaveIntent
            {
                Nonce = nonce,
                Id = id,
                Url = url,
                Filename = filename!,
                Size = size,
                Sha256 = sha
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return null;
        }
    }

    internal static bool ValidFilename(string? filename) =>
        filename is { Length: > 0 and <= 160 } && filename is not ("." or "..")
                                               && !filename.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character, StringComparison.Ordinal));
}
