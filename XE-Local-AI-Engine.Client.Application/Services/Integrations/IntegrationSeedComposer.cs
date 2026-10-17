namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Turns an invoke body's inputs into the single seed user turn the run starts from, in caller order.
/// </summary>
/// <remarks>
///     A JSON input is ALWAYS fenced with <c>UntrustedContentFraming.WrapDocument</c>, never concatenated raw: it is
///     attacker-controlled data arriving over an API, and its label is attacker-controlled too, so both go inside one
///     fence whose per-call nonce the author cannot predict. It does NOT truncate — the caller enforces the seed
///     ceiling and answers 422, because silently trimming an external payload changes the meaning of the request.
/// </remarks>
public static class IntegrationSeedComposer
{
    private const string BlockSeparator = "\n\n";

    public static string Compose(IReadOnlyList<IntegrationInputDto> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var blocks = new List<string>(inputs.Count);
        foreach (var input in inputs)
        {
            if (input.Kind == IntegrationInputKinds.Json)
            {
                blocks.Add(UntrustedContentFraming.WrapDocument(input.Json,
                [
                    new KeyValuePair<string, string?>("label", input.Label ?? "json"),
                    new KeyValuePair<string, string?>("contentType", "application/json")
                ]));
                continue;
            }

            blocks.Add(input.Text?.Trim() ?? string.Empty);
        }

        return string.Join(BlockSeparator, blocks);
    }

    /// <summary>
    ///     A graph target's run input: <c>{ "input": text | null, "documents": [{ "label", "json" }] }</c>, in caller order.
    /// </summary>
    /// <remarks>
    ///     The text inputs joined by a blank line, or null when there are none; each JSON input verbatim under its label. No
    ///     fence: a graph reads the documents as data through bindings, and a node that hands them to a model frames them
    ///     there. It is stored as the seed turn, so the seed ceiling bounds it like any other.
    /// </remarks>
    public static string ComposeGraphInput(IReadOnlyList<IntegrationInputDto> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var texts = inputs.Where(static input => input.Kind == IntegrationInputKinds.Text).Select(static input => input.Text?.Trim() ?? string.Empty).ToList();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (texts.Count == 0)
            {
                writer.WriteNull("input");
            }
            else
            {
                writer.WriteString("input", string.Join(BlockSeparator, texts));
            }

            writer.WriteStartArray("documents");
            foreach (var input in inputs.Where(static input => input.Kind == IntegrationInputKinds.Json))
            {
                writer.WriteStartObject();
                writer.WriteString("label", input.Label ?? "json");
                writer.WritePropertyName("json");
                writer.WriteRawValue(input.Json ?? "null");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The composed seed's size against the configured ceiling, measured the way the ceiling is expressed.</summary>
    public static int Utf8ByteCount(string seed) =>
        Encoding.UTF8.GetByteCount(seed);
}
