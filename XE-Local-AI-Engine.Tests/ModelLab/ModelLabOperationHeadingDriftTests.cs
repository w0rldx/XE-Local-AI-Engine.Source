namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Every operation heading of every registered descriptor, block kind and Q/K/V layout has German text under
///     <c>pages.modelLab.architecture.opLabel</c>, found in the client's candidate order, and no German text stands for two English labels.
/// </summary>
/// <remarks>
///     The order copies <c>operationLabelCandidates</c> in <c>ModelLabExplanation.ts</c>: per architecture and then its fallback,
///     <c>byKind.&lt;arch&gt;.&lt;kind&gt;.&lt;id&gt;</c> and <c>arch.&lt;arch&gt;.&lt;id&gt;</c>, then <c>any.&lt;id&gt;</c>; each key first with the
///     <c>_&lt;layout&gt;</c> suffix. A vitest pins the same fallback map on the client side.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ModelLabOperationHeadingDriftTests
{
    private const string ModelName = "org/Some-GGUF:Q4_K_M";

    // Four families: qwen35 and qwen35moe draw linear attention once and full attention and MTP per layout (7 each), llama and olmoe one per layout.
    private const int MinimumTemplates = 20;

    private static readonly string[] Layouts = ["separate", "fused", "mixed"];

    // The client's architectureFallbacks.
    private static readonly Dictionary<string, string> ArchitectureFallbacks = new(StringComparer.Ordinal) { ["qwen35moe"] = "qwen35" };

    [Test]
    public async Task EveryHeading_HasGermanText_AndEveryKeyReached_CoversOneEnglishLabel()
    {
        var opLabel = await GermanOpLabelsAsync();
        var headings = Headings();

        var missing = new List<string>();
        var labelsByKey = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var heading in headings)
        {
            if (Resolve(opLabel, heading) is not { } key)
            {
                missing.Add(heading.ToString());
                continue;
            }

            if (!labelsByKey.TryGetValue(key, out var labels))
            {
                labels = new SortedSet<string>(StringComparer.Ordinal);
                labelsByKey[key] = labels;
            }

            labels.Add(heading.Label);
        }

        AssertEx.Empty(missing, "operation headings without German text");
        AssertEx.Empty(labelsByKey.Where(static pair => pair.Value.Count > 1).Select(static pair => $"{pair.Key}: {string.Join(" | ", pair.Value)}"),
            "keys reached by nodes with different English labels");
        // A key no heading reaches is a typo or a leftover, and would hide a missing one.
        AssertEx.Empty(LeafPaths(opLabel, prefix: null).Where(path => !labelsByKey.ContainsKey(path)), "German headings no node reaches");
    }

    [Test]
    public async Task ALayoutDependentId_HasOnlySuffixedKeys_AndNoNodeIdEndsInALayout()
    {
        var opLabel = await GermanOpLabelsAsync();
        var headings = Headings();

        // An id whose English label differs between the layouts of one architecture and kind.
        var layoutDependent = headings.GroupBy(static heading => $"{heading.Architecture}|{heading.Kind}|{heading.Id}", StringComparer.Ordinal)
                                      .Where(static group => group.Select(static heading => heading.Label).Distinct(StringComparer.Ordinal).Count() > 1)
                                      .Select(static group => group.First())
                                      .ToList();

        AssertEx.NotEmpty(layoutDependent);
        AssertEx.Empty(layoutDependent.SelectMany(heading => Candidates(heading.Architecture, heading.Kind, heading.Id, layout: null))
                                      .Where(candidate => TextAt(opLabel, candidate) is not null), "unsuffixed keys of layout-dependent ids");
        AssertEx.Empty(headings.Select(static heading => heading.Id).Distinct(StringComparer.Ordinal)
                               .Where(static id => Layouts.Any(layout => id.EndsWith("_" + layout, StringComparison.Ordinal))), "node ids ending in a layout suffix");
    }

    // The key of the first candidate with German text, as the client resolves it.
    private static string? Resolve(JsonElement opLabel, Heading heading) =>
        Candidates(heading.Architecture, heading.Kind, heading.Id, heading.Layout).FirstOrDefault(candidate => TextAt(opLabel, candidate) is not null);

    private static IEnumerable<string> Candidates(string architecture, string kind, string id, string? layout)
    {
        string[] architectures = ArchitectureFallbacks.TryGetValue(architecture, out var fallback) ? [architecture, fallback] : [architecture];
        var keys = architectures.SelectMany(name => new[] { $"byKind.{name}.{kind}.{id}", $"arch.{name}.{id}" }).Append($"any.{id}");
        return keys.SelectMany(key => layout is null ? new[] { key } : [$"{key}_{layout}", key]);
    }

    private static string? TextAt(JsonElement root, string path)
    {
        var element = root;
        foreach (var segment in path.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String && element.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    private static IEnumerable<string> LeafPaths(JsonElement element, string? prefix) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().SelectMany(property => LeafPaths(property.Value, prefix is null ? property.Name : $"{prefix}.{property.Name}"))
            : [prefix ?? string.Empty];

    private static async Task<JsonElement> GermanOpLabelsAsync()
    {
        var path = RepositoryPaths.Combine("XE-Local-AI-Engine.Client.React", "src", "features", "modelLab", "locales", "model-lab-de.json");
        using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return bundle.RootElement.GetProperty("pages").GetProperty("modelLab").GetProperty("architecture").GetProperty("opLabel").Clone();
    }

    // Every node of every template each registered descriptor builds, for each Q/K/V layout of its full-attention and MTP blocks.
    private static List<Heading> Headings()
    {
        var templates = new Dictionary<string, Heading[]>(StringComparer.Ordinal);
        var withoutTemplates = new List<string>();
        foreach (var architecture in ModelLabArchitectureBuilder.RegisteredDescriptors.Select(static descriptor => descriptor.Architecture))
        {
            var built = false;
            foreach (var layout in Layouts)
            {
                foreach (var template in ModelLabArchitectureBuilder.Build(ModelName, Qwen35TensorFixture.Inspection(architecture, Tensors(layout))).Templates)
                {
                    built = true;
                    templates[$"{architecture}|{template.Kind}|{template.QkvLayout}"] =
                    [
                        .. template.Nodes.Select(node => new Heading
                        {
                            Architecture = architecture,
                            Kind = template.Kind,
                            Layout = template.QkvLayout,
                            Id = node.Id,
                            Label = node.Label
                        })
                    ];
                }
            }

            if (!built)
            {
                withoutTemplates.Add(architecture);
            }
        }

        // A family the fixture tensors draw no template for would pass with none of its headings checked.
        AssertEx.True(withoutTemplates.Count == 0,
            $"registered descriptors that build no template from Tensors(), so none of their headings is checked; add a fixture for: {string.Join(", ", withoutTemplates)}");
        AssertEx.True(templates.Count >= MinimumTemplates, $"only {templates.Count} templates enumerated: {string.Join(", ", templates.Keys)}");
        return [.. templates.Values.SelectMany(static nodes => nodes)];
    }

    // A dense file (no expert tensor, so llama draws its template) with a linear block, two full-attention blocks and two MTP blocks;
    // "mixed" needs two blocks of a kind, one fused and one separate.
    private static IEnumerable<GgufTensorInfo> Tensors(string layout)
    {
        string[] Qkv(int block) =>
            (layout == "fused" || (layout == "mixed" && block % 2 == 0)) ? ["attn_qkv.weight"] : ["attn_q.weight", "attn_k.weight", "attn_v.weight"];

        var names = new[] { "blk.0.ssm_a", "blk.0.attn_qkv.weight" }
                    .Concat(new[] { 1, 2 }.SelectMany(block => Qkv(block).Append("attn_norm.weight").Select(suffix => $"blk.{block}.{suffix}")))
                    .Concat(new[] { 3, 4 }.SelectMany(block => Qkv(block).Append("nextn.eh_proj.weight").Select(suffix => $"blk.{block}.{suffix}")));
        return names.Select(static name => Qwen35TensorFixture.Tensor(name, [64], "F32"));
    }

    private sealed class Heading
    {
        public required string Architecture { get; init; }

        public required string Kind { get; init; }

        public required string? Layout { get; init; }

        public required string Id { get; init; }

        public required string Label { get; init; }

        public override string ToString() => $"{Architecture}/{Kind}/{Layout ?? "-"}/{Id}: {Label}";
    }
}
